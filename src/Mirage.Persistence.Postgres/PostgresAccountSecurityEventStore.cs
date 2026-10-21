using Mirage.Domain.Accounts;
using Npgsql;
using NpgsqlTypes;

namespace Mirage.Persistence.Postgres;

public sealed class PostgresAccountSecurityEventStore(NpgsqlDataSource dataSource)
    : IAccountSecurityEventStore
{
    public async ValueTask AppendAsync(
        AccountSecurityEvent securityEvent,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO account_security_events (
                security_event_id, event_type, account_id, attempted_user_name,
                source_address, occurred_at)
            VALUES ($1, $2, $3, $4, $5, $6)
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(securityEvent.EventId);
        command.Parameters.AddWithValue(securityEvent.EventType.ToString());
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = securityEvent.AccountId is { } accountId ? accountId : DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Varchar,
            Value = securityEvent.AttemptedUserName is { } userName ? userName : DBNull.Value
        });
        command.Parameters.AddWithValue(securityEvent.SourceAddress);
        command.Parameters.AddWithValue(securityEvent.OccurredAt.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<AccountSecurityEvent>> ListRecentAsync(
        Guid? accountId,
        AccountSecurityEventType? eventType,
        DateTimeOffset? before,
        int limit,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT security_event_id, event_type, account_id, attempted_user_name,
                   source_address, occurred_at
            FROM account_security_events
            WHERE ($1::uuid IS NULL OR account_id = $1)
              AND ($2::varchar IS NULL OR event_type = $2)
              AND ($3::timestamptz IS NULL OR occurred_at < $3)
            ORDER BY occurred_at DESC, security_event_id DESC
            LIMIT $4
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = accountId is { } selectedAccountId ? selectedAccountId : DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Varchar,
            Value = eventType is { } selectedEventType ? selectedEventType.ToString() : DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
            Value = before is { } cursor ? cursor.UtcDateTime : DBNull.Value
        });
        command.Parameters.AddWithValue(Math.Clamp(limit, 1, AccountSecurityEventPolicy.MaximumQueryLimit));

        var events = new List<AccountSecurityEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var storedType = reader.GetString(1);
            if (!Enum.TryParse<AccountSecurityEventType>(storedType, out var parsedType))
            {
                throw new InvalidOperationException($"Unknown account security event type: {storedType}");
            }

            events.Add(new AccountSecurityEvent(
                reader.GetGuid(0),
                parsedType,
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero)));
        }

        return events;
    }
}
