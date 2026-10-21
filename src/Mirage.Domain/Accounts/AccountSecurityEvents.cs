using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Mirage.Domain.Accounts;

public enum AccountSecurityEventType
{
    RegistrationSucceeded = 1,
    RegistrationRejected = 2,
    LoginSucceeded = 3,
    LoginFailed = 4,
    SessionRevoked = 5,
    SessionRevokeFailed = 6,
    AuthenticationRateLimited = 7
}

public sealed record AccountSecurityEvent(
    Guid EventId,
    AccountSecurityEventType EventType,
    Guid? AccountId,
    string? AttemptedUserName,
    string SourceAddress,
    DateTimeOffset OccurredAt)
{
    public static AccountSecurityEvent Create(
        AccountSecurityEventType eventType,
        Guid? accountId,
        string? attemptedUserName,
        string? sourceAddress,
        DateTimeOffset occurredAt)
    {
        var normalizedUserName = AccountPolicy.NormalizeUserName(attemptedUserName);
        if (normalizedUserName.Length > AccountSecurityEventPolicy.MaximumAttemptedUserNameLength)
        {
            normalizedUserName = normalizedUserName[..AccountSecurityEventPolicy.MaximumAttemptedUserNameLength];
        }

        var normalizedSource = string.IsNullOrWhiteSpace(sourceAddress)
            ? "unknown"
            : sourceAddress.Trim();
        if (normalizedSource.Length > AccountSecurityEventPolicy.MaximumSourceAddressLength)
        {
            normalizedSource = normalizedSource[..AccountSecurityEventPolicy.MaximumSourceAddressLength];
        }

        return new AccountSecurityEvent(
            Guid.NewGuid(),
            eventType,
            accountId,
            string.IsNullOrEmpty(normalizedUserName) ? null : normalizedUserName,
            normalizedSource,
            occurredAt);
    }
}

public static class AccountSecurityEventPolicy
{
    public const int MaximumQueryLimit = 200;
    public const int MaximumRetainedInMemory = 10_000;
    public const int MaximumAttemptedUserNameLength = 64;
    public const int MaximumSourceAddressLength = 64;
}

public interface IAccountSecurityEventStore
{
    ValueTask AppendAsync(AccountSecurityEvent securityEvent, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<AccountSecurityEvent>> ListRecentAsync(
        Guid? accountId,
        AccountSecurityEventType? eventType,
        DateTimeOffset? before,
        int limit,
        CancellationToken cancellationToken);
}

public sealed class InMemoryAccountSecurityEventStore : IAccountSecurityEventStore
{
    private readonly ConcurrentQueue<AccountSecurityEvent> _events = new();

    public ValueTask AppendAsync(
        AccountSecurityEvent securityEvent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Enqueue(securityEvent);
        while (_events.Count > AccountSecurityEventPolicy.MaximumRetainedInMemory)
        {
            _events.TryDequeue(out _);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<AccountSecurityEvent>> ListRecentAsync(
        Guid? accountId,
        AccountSecurityEventType? eventType,
        DateTimeOffset? before,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var boundedLimit = Math.Clamp(limit, 1, AccountSecurityEventPolicy.MaximumQueryLimit);
        IReadOnlyList<AccountSecurityEvent> result = _events
            .Where(value => !accountId.HasValue || value.AccountId == accountId)
            .Where(value => !eventType.HasValue || value.EventType == eventType)
            .Where(value => !before.HasValue || value.OccurredAt < before)
            .OrderByDescending(value => value.OccurredAt)
            .ThenByDescending(value => value.EventId)
            .Take(boundedLimit)
            .ToArray();
        return ValueTask.FromResult(result);
    }
}

public static class SecuritySecretVerifier
{
    public static bool IsMatch(string? presentedSecret, string? configuredSecret)
    {
        if (string.IsNullOrEmpty(presentedSecret) || string.IsNullOrEmpty(configuredSecret))
        {
            return false;
        }

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presentedSecret));
        var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configuredSecret));
        return CryptographicOperations.FixedTimeEquals(presentedHash, configuredHash);
    }
}
