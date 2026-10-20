using System.Security.Cryptography;
using System.Text;
using Mirage.Domain.Accounts;
using Npgsql;

namespace Mirage.Persistence.Postgres;

public sealed class PostgresSessionStore : ISessionStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeSpan _lifetime;

    public PostgresSessionStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
        _lifetime = TimeSpan.FromMinutes(30);
    }

    public async ValueTask<IssuedSession> IssueAsync(
        Account account,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO sessions (token_hash, account_id, expires_at, created_at)
            VALUES ($1, $2, $3, $4)
            """;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = ToBase64Url(RandomNumberGenerator.GetBytes(32));
            var expiresAt = now.Add(_lifetime);
            await using var command = _dataSource.CreateCommand(sql);
            command.Parameters.AddWithValue(HashToken(token));
            command.Parameters.AddWithValue(account.Id);
            command.Parameters.AddWithValue(expiresAt.UtcDateTime);
            command.Parameters.AddWithValue(now.UtcDateTime);

            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
                return new IssuedSession(account.Id, account.NormalizedUserName, token, expiresAt);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Cryptographic collision: generate another token.
            }
        }
    }

    public async ValueTask<SessionPrincipal?> FindActiveAsync(
        string accessToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        const string sql = """
            SELECT s.account_id, a.normalized_user_name, s.expires_at
            FROM sessions AS s
            INNER JOIN accounts AS a ON a.account_id = s.account_id
            WHERE s.token_hash = $1
              AND s.revoked_at IS NULL
              AND s.expires_at > $2
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(HashToken(accessToken));
        command.Parameters.AddWithValue(now.UtcDateTime);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SessionPrincipal(
                reader.GetGuid(0),
                reader.GetString(1),
                new DateTimeOffset(reader.GetDateTime(2), TimeSpan.Zero))
            : null;
    }

    public async ValueTask<bool> RevokeAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return false;
        }

        const string sql = """
            UPDATE sessions
            SET revoked_at = CURRENT_TIMESTAMP
            WHERE token_hash = $1 AND revoked_at IS NULL AND expires_at > CURRENT_TIMESTAMP
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(HashToken(accessToken));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static byte[] HashToken(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
