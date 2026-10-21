using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Mirage.Domain.Accounts;
using Mirage.Domain.Common;
using Mirage.Domain.Characters;
using Mirage.Persistence.Postgres;
using Mirage.Domain.World;
using Mirage.AuthServer;
using Mirage.Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<AccountService>();
builder.Services.AddSingleton<CharacterService>();
builder.Services.AddHostedService<CharacterDeletionCleanupService>();

var ticketProtector = CreateTicketProtector(builder.Configuration);
if (ticketProtector is not null)
{
    builder.Services.AddSingleton(ticketProtector);
}

var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(postgresConnectionString))
{
    builder.Services.AddSingleton<IAccountRepository, InMemoryAccountRepository>();
    builder.Services.AddSingleton<ISessionStore, InMemorySessionStore>();
    builder.Services.AddSingleton<ICharacterRepository, InMemoryCharacterRepository>();
}
else
{
    var dataSource = PostgresStorage.CreateDataSource(postgresConnectionString);
    builder.Services.AddSingleton(dataSource);
    builder.Services.AddSingleton<IAccountRepository, PostgresAccountRepository>();
    builder.Services.AddSingleton<ISessionStore, PostgresSessionStore>();
    builder.Services.AddSingleton<ICharacterRepository, PostgresCharacterRepository>();
}
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("authentication", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

var app = builder.Build();
if (!string.IsNullOrWhiteSpace(postgresConnectionString)
    && builder.Configuration.GetValue<bool>("Database:ApplyMigrations"))
{
    await PostgresMigrationRunner.ApplyAsync(
        app.Services.GetRequiredService<Npgsql.NpgsqlDataSource>(),
        app.Lifetime.ApplicationStopping);
}
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "Mirage.AuthServer",
    utc = DateTimeOffset.UtcNow
}));

app.MapPost("/api/v1/accounts", async (
    RegisterAccountRequest request,
    AccountService accounts,
    CancellationToken cancellationToken) =>
{
    var result = await accounts.RegisterAsync(request.UserName, request.Password, cancellationToken);
    if (!result.IsSuccess)
    {
        return ToHttpError(result.Error!);
    }

    var account = result.Value!;
    return Results.Created(
        $"/api/v1/accounts/{account.Id}",
        new AccountResponse(account.Id, account.NormalizedUserName, account.CreatedAt));
}).RequireRateLimiting("authentication");

app.MapPost("/api/v1/sessions", async (
    LoginRequest request,
    AccountService accounts,
    CancellationToken cancellationToken) =>
{
    var result = await accounts.LoginAsync(request.UserName, request.Password, cancellationToken);
    if (!result.IsSuccess)
    {
        return ToHttpError(result.Error!);
    }

    var session = result.Value!;
    return Results.Ok(new SessionResponse(
        session.AccountId,
        session.UserName,
        session.AccessToken,
        session.ExpiresAt));
}).RequireRateLimiting("authentication");

app.MapGet("/api/v1/sessions/current", async (
    HttpRequest request,
    AccountService accounts,
    CancellationToken cancellationToken) =>
{
    var result = await accounts.ValidateSessionAsync(ReadBearerToken(request), cancellationToken);
    if (!result.IsSuccess)
    {
        return ToHttpError(result.Error!);
    }

    var session = result.Value!;
    return Results.Ok(new SessionStatusResponse(
        session.AccountId,
        session.UserName,
        session.ExpiresAt));
});

app.MapDelete("/api/v1/sessions/current", async (
    HttpRequest request,
    AccountService accounts,
    CancellationToken cancellationToken) =>
{
    var result = await accounts.RevokeSessionAsync(ReadBearerToken(request), cancellationToken);
    return result.IsSuccess ? Results.NoContent() : ToHttpError(result.Error!);
});

app.MapGet("/api/v1/realms", () => Results.Ok(new[]
{
    new RealmResponse("local", "本地开发区", "Online", "127.0.0.1", 7000, 1, 1)
}));

app.MapGet("/api/v1/characters", async (
    HttpRequest request,
    AccountService accounts,
    CharacterService characters,
    CancellationToken cancellationToken) =>
{
    var authentication = await accounts.ValidateSessionAsync(ReadBearerToken(request), cancellationToken);
    if (!authentication.IsSuccess)
    {
        return ToHttpError(authentication.Error!);
    }

    var values = await characters.ListAsync(authentication.Value!.AccountId, cancellationToken);
    return Results.Ok(values.Select(ToCharacterResponse));
});

app.MapPost("/api/v1/characters", async (
    HttpRequest httpRequest,
    CreateCharacterRequest request,
    AccountService accounts,
    CharacterService characters,
    CancellationToken cancellationToken) =>
{
    var authentication = await accounts.ValidateSessionAsync(ReadBearerToken(httpRequest), cancellationToken);
    if (!authentication.IsSuccess)
    {
        return ToHttpError(authentication.Error!);
    }

    var result = await characters.CreateAsync(
        authentication.Value!.AccountId,
        request.Name,
        request.Archetype,
        cancellationToken);
    return result.IsSuccess
        ? Results.Created($"/api/v1/characters/{result.Value!.Id}", ToCharacterResponse(result.Value))
        : ToHttpError(result.Error!);
});

app.MapPost("/api/v1/characters/{characterId:guid}/deletion", async (
    Guid characterId,
    RequestCharacterDeletionRequest request,
    HttpRequest httpRequest,
    AccountService accounts,
    CharacterService characters,
    CancellationToken cancellationToken) =>
{
    var authentication = await accounts.ValidateSessionAsync(ReadBearerToken(httpRequest), cancellationToken);
    if (!authentication.IsSuccess)
    {
        return ToHttpError(authentication.Error!);
    }

    var result = await characters.RequestDeletionAsync(
        authentication.Value!.AccountId,
        characterId,
        request.ConfirmationName,
        cancellationToken);
    return result.IsSuccess ? Results.Ok(ToCharacterResponse(result.Value!)) : ToHttpError(result.Error!);
});

app.MapDelete("/api/v1/characters/{characterId:guid}/deletion", async (
    Guid characterId,
    HttpRequest httpRequest,
    AccountService accounts,
    CharacterService characters,
    CancellationToken cancellationToken) =>
{
    var authentication = await accounts.ValidateSessionAsync(ReadBearerToken(httpRequest), cancellationToken);
    if (!authentication.IsSuccess)
    {
        return ToHttpError(authentication.Error!);
    }

    var result = await characters.CancelDeletionAsync(
        authentication.Value!.AccountId,
        characterId,
        cancellationToken);
    return result.IsSuccess ? Results.Ok(ToCharacterResponse(result.Value!)) : ToHttpError(result.Error!);
});

app.MapPost("/api/v1/characters/{characterId:guid}/entry-ticket", async (
    Guid characterId,
    WorldEntryTicketRequest request,
    HttpRequest httpRequest,
    AccountService accounts,
    CharacterService characters,
    IServiceProvider services,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var authentication = await accounts.ValidateSessionAsync(
        ReadBearerToken(httpRequest),
        cancellationToken);
    if (!authentication.IsSuccess)
    {
        return ToHttpError(authentication.Error!);
    }

    var realmId = string.IsNullOrWhiteSpace(request.RealmId) ? "local" : request.RealmId;
    if (!string.Equals(realmId, "local", StringComparison.Ordinal))
    {
        return Results.BadRequest(new ApiError("InvalidRealm", "区服不存在。"));
    }

    var character = await characters.FindOwnedAsync(
        authentication.Value!.AccountId,
        characterId,
        cancellationToken);
    if (character is null)
    {
        return Results.NotFound(new ApiError("CharacterNotFound", "角色不存在。"));
    }

    if (character.IsPendingDeletion)
    {
        return ToHttpError(new DomainError(ErrorCode.CharacterPendingDeletion, "待删除角色不能进入游戏。"));
    }

    var protector = services.GetService<HmacWorldEntryTicketProtector>();
    if (protector is null)
    {
        return Results.Json(
            new ApiError("TicketServiceUnavailable", "世界服票据服务尚未配置。"),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var issued = protector.Issue(
        authentication.Value.AccountId,
        character.Id,
        realmId,
        DateTimeOffset.UtcNow,
        character.MapId,
        character.PositionX,
        character.PositionY,
        character.Archetype);
    var webSocketUrl = configuration["WorldEntryTickets:WebSocketUrl"]
        ?? "ws://127.0.0.1:5090/ws/v1/world";
    return Results.Ok(new WorldEntryTicketResponse(
        character.Id,
        realmId,
        webSocketUrl,
        issued.Token,
        issued.Claims.ExpiresAt));
});

app.Run();

static IResult ToHttpError(DomainError error)
{
    var body = new ApiError(error.Code.ToString(), error.Message);
    return error.Code switch
    {
        ErrorCode.AccountAlreadyExists or ErrorCode.CharacterNameAlreadyExists or ErrorCode.CharacterLimitReached =>
            Results.Conflict(body),
        ErrorCode.CharacterPendingDeletion or ErrorCode.CharacterDeletionNotPending =>
            Results.Conflict(body),
        ErrorCode.CharacterNotFound => Results.NotFound(body),
        ErrorCode.InvalidCredentials or ErrorCode.InvalidSession =>
            Results.Json(body, statusCode: StatusCodes.Status401Unauthorized),
        _ => Results.BadRequest(body)
    };
}

static CharacterResponse ToCharacterResponse(GameCharacter character) =>
    new(
        character.Id,
        character.Name,
        character.Archetype,
        character.Level,
        character.CreatedAt,
        character.DeletionRequestedAt,
        character.DeleteAfter);

static string? ReadBearerToken(HttpRequest request)
{
    const string prefix = "Bearer ";
    var authorization = request.Headers.Authorization.ToString();
    return authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        ? authorization[prefix.Length..].Trim()
        : null;
}

static HmacWorldEntryTicketProtector? CreateTicketProtector(IConfiguration configuration)
{
    var encodedKey = configuration["WorldEntryTickets:SigningKeyBase64"];
    if (string.IsNullOrWhiteSpace(encodedKey))
    {
        return null;
    }

    try
    {
        return new HmacWorldEntryTicketProtector(Convert.FromBase64String(encodedKey));
    }
    catch (FormatException)
    {
        throw new InvalidOperationException("World entry ticket signing key is not valid Base64.");
    }
}
