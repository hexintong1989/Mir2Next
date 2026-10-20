using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Mirage.Domain.Accounts;

public sealed record GameSession(
    Guid AccountId,
    string UserName,
    string AccessToken,
    DateTimeOffset ExpiresAt);

public interface ISessionIssuer
{
    GameSession Issue(Account account, DateTimeOffset now);
}

public sealed class InMemorySessionIssuer : ISessionIssuer
{
    private readonly ConcurrentDictionary<string, GameSession> _sessions = new(StringComparer.Ordinal);
    private readonly TimeSpan _lifetime;

    public InMemorySessionIssuer(TimeSpan? lifetime = null)
    {
        _lifetime = lifetime ?? TimeSpan.FromMinutes(30);
    }

    public GameSession Issue(Account account, DateTimeOffset now)
    {
        while (true)
        {
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            var session = new GameSession(
                account.Id,
                account.NormalizedUserName,
                token,
                now.Add(_lifetime));

            if (_sessions.TryAdd(token, session))
            {
                return session;
            }
        }
    }
}
