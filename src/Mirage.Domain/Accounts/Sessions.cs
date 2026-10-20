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
    IssuedSession Issue(Account account, DateTimeOffset now);
    SessionPrincipal? FindActive(string accessToken, DateTimeOffset now);
    bool Revoke(string accessToken);
}

public sealed class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, SessionPrincipal> _sessions = new(StringComparer.Ordinal);
    private readonly TimeSpan _lifetime;

    public InMemorySessionStore(TimeSpan? lifetime = null)
    {
        _lifetime = lifetime ?? TimeSpan.FromMinutes(30);
    }

    public IssuedSession Issue(Account account, DateTimeOffset now)
    {
        while (true)
        {
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
                return new IssuedSession(
                    principal.AccountId,
                    principal.UserName,
                    token,
                    principal.ExpiresAt);
            }
        }
    }

    public SessionPrincipal? FindActive(string accessToken, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        var key = HashToken(accessToken);
        if (!_sessions.TryGetValue(key, out var session))
        {
            return null;
        }

        if (session.ExpiresAt <= now)
        {
            _sessions.TryRemove(key, out _);
            return null;
        }

        return session;
    }

    public bool Revoke(string accessToken) =>
        !string.IsNullOrWhiteSpace(accessToken)
        && _sessions.TryRemove(HashToken(accessToken), out _);

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
