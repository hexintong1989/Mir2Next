using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Mirage.Domain.Accounts;
using Mirage.Domain.Common;
using Mirage.Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<IAccountRepository, InMemoryAccountRepository>();
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<ISessionStore, InMemorySessionStore>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<AccountService>();
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

app.MapGet("/api/v1/sessions/current", (HttpRequest request, AccountService accounts) =>
{
    var result = accounts.ValidateSession(ReadBearerToken(request));
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

app.MapDelete("/api/v1/sessions/current", (HttpRequest request, AccountService accounts) =>
{
    var result = accounts.RevokeSession(ReadBearerToken(request));
    return result.IsSuccess ? Results.NoContent() : ToHttpError(result.Error!);
});

app.Run();

static IResult ToHttpError(DomainError error)
{
    var body = new ApiError(error.Code.ToString(), error.Message);
    return error.Code switch
    {
        ErrorCode.AccountAlreadyExists => Results.Conflict(body),
        ErrorCode.InvalidCredentials or ErrorCode.InvalidSession =>
            Results.Json(body, statusCode: StatusCodes.Status401Unauthorized),
        _ => Results.BadRequest(body)
    };
}

static string? ReadBearerToken(HttpRequest request)
{
    const string prefix = "Bearer ";
    var authorization = request.Headers.Authorization.ToString();
    return authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        ? authorization[prefix.Length..].Trim()
        : null;
}
