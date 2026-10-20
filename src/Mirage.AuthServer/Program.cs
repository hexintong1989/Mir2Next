using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Mirage.Domain.Accounts;
using Mirage.Domain.Common;
using Mirage.Domain.Characters;
using Mirage.Persistence.Postgres;
using Mirage.Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<AccountService>();
builder.Services.AddSingleton<CharacterService>();

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

app.Run();

static IResult ToHttpError(DomainError error)
{
    var body = new ApiError(error.Code.ToString(), error.Message);
    return error.Code switch
    {
        ErrorCode.AccountAlreadyExists or ErrorCode.CharacterNameAlreadyExists or ErrorCode.CharacterLimitReached =>
            Results.Conflict(body),
        ErrorCode.InvalidCredentials or ErrorCode.InvalidSession =>
            Results.Json(body, statusCode: StatusCodes.Status401Unauthorized),
        _ => Results.BadRequest(body)
    };
}

static CharacterResponse ToCharacterResponse(GameCharacter character) =>
    new(character.Id, character.Name, character.Archetype, character.Level, character.CreatedAt);

static string? ReadBearerToken(HttpRequest request)
{
    const string prefix = "Bearer ";
    var authorization = request.Headers.Authorization.ToString();
    return authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        ? authorization[prefix.Length..].Trim()
        : null;
}
