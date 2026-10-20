using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Mirage.Domain.Accounts;

public sealed record IssuedSession(
    Guid AccountId,
    string UserName,
    string AccessToken,
    DateTimeOffset ExpiresAt);

public sealed record SessionPrincipal(
    Guid AccountId,
    string UserName,
    DateTimeOffset ExpiresAt);

public interface ISessionStore
{
    ValueTask<IssuedSession> IssueAsync(
        Account account,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<SessionPrincipal?> FindActiveAsync(
        string accessToken,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<bool> RevokeAsync(string accessToken, CancellationToken cancellationToken);
}

public sealed class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, SessionPrincipal> _sessions = new(StringComparer.Ordinal);
    private readonly TimeSpan _lifetime;

    public InMemorySessionStore(TimeSpan? lifetime = null)
    {
        _lifetime = lifetime ?? TimeSpan.FromMinutes(30);
    }

    public ValueTask<IssuedSession> IssueAsync(
        Account account,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            var principal = new SessionPrincipal(
                account.Id,
                account.NormalizedUserName,
                now.Add(_lifetime));

            if (_sessions.TryAdd(HashToken(token), principal))
            {
                return ValueTask.FromResult(new IssuedSession(
                    principal.AccountId,
                    principal.UserName,
                    token,
                    principal.ExpiresAt));
            }
        }
    }

    public ValueTask<SessionPrincipal?> FindActiveAsync(
        string accessToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return ValueTask.FromResult<SessionPrincipal?>(null);
        }

        var key = HashToken(accessToken);
        if (!_sessions.TryGetValue(key, out var session))
        {
            return ValueTask.FromResult<SessionPrincipal?>(null);
        }

        if (session.ExpiresAt <= now)
        {
            _sessions.TryRemove(key, out _);
            return ValueTask.FromResult<SessionPrincipal?>(null);
        }

        return ValueTask.FromResult<SessionPrincipal?>(session);
    }

    public ValueTask<bool> RevokeAsync(string accessToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            !string.IsNullOrWhiteSpace(accessToken)
            && _sessions.TryRemove(HashToken(accessToken), out _));
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
