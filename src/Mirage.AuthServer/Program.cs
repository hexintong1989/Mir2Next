using System.Text.Json.Serialization;
using Mirage.Domain.Accounts;
using Mirage.Domain.Common;
using Mirage.Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<IAccountRepository, InMemoryAccountRepository>();
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<ISessionIssuer, InMemorySessionIssuer>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<AccountService>();

var app = builder.Build();

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
});

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
});

app.Run();

static IResult ToHttpError(DomainError error)
{
    var body = new ApiError(error.Code.ToString(), error.Message);
    return error.Code switch
    {
        ErrorCode.AccountAlreadyExists => Results.Conflict(body),
        ErrorCode.InvalidCredentials => Results.Json(body, statusCode: StatusCodes.Status401Unauthorized),
        _ => Results.BadRequest(body)
    };
}
