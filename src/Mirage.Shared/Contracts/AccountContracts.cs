namespace Mirage.Shared.Contracts;

public sealed record RegisterAccountRequest(string? UserName, string? Password);

public sealed record LoginRequest(string? UserName, string? Password);

public sealed record AccountResponse(Guid AccountId, string UserName, DateTimeOffset CreatedAt);

public sealed record SessionResponse(
    Guid AccountId,
    string UserName,
    string AccessToken,
    DateTimeOffset ExpiresAt);

public sealed record SessionStatusResponse(
    Guid AccountId,
    string UserName,
    DateTimeOffset ExpiresAt);

public sealed record AccountSecurityEventResponse(
    Guid EventId,
    string EventType,
    Guid? AccountId,
    string? AttemptedUserName,
    string SourceAddress,
    DateTimeOffset OccurredAt);

public sealed record ApiError(string Code, string Message);
