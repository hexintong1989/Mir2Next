using System.Data;
using Mirage.Domain.Characters;
using Mirage.Shared.Contracts;
using Npgsql;

namespace Mirage.Persistence.Postgres;

public sealed class PostgresCharacterRepository(NpgsqlDataSource dataSource) : ICharacterRepository
{
    public async ValueTask<GameCharacter?> FindOwnedAsync(
        Guid accountId,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT character_id, account_id, name, normalized_name, archetype, level, created_at,
                   deletion_requested_at, delete_after
            FROM characters
            WHERE account_id = $1 AND character_id = $2
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(accountId);
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCharacter(reader) : null;
    }

    public async ValueTask<IReadOnlyList<GameCharacter>> ListByAccountAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT character_id, account_id, name, normalized_name, archetype, level, created_at,
                   deletion_requested_at, delete_after
            FROM characters
            WHERE account_id = $1
            ORDER BY created_at, character_id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var characters = new List<GameCharacter>();
        while (await reader.ReadAsync(cancellationToken))
        {
            characters.Add(ReadCharacter(reader));
        }

        return characters;
    }

    public async ValueTask<AddCharacterOutcome> TryAddAsync(
        GameCharacter character,
        int accountLimit,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        await using (var accountLock = connection.CreateCommand())
        {
            accountLock.Transaction = transaction;
            accountLock.CommandText = "SELECT account_id FROM accounts WHERE account_id = $1 FOR UPDATE";
            accountLock.Parameters.AddWithValue(character.AccountId);
            if (await accountLock.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw new InvalidOperationException("Cannot create a character for an unknown account.");
            }
        }

        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.Transaction = transaction;
            countCommand.CommandText = "SELECT COUNT(*) FROM characters WHERE account_id = $1";
            countCommand.Parameters.AddWithValue(character.AccountId);
            var count = (long)(await countCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);
            if (count >= accountLimit)
            {
                await transaction.RollbackAsync(cancellationToken);
                return AddCharacterOutcome.AccountLimitReached;
            }
        }

        const string insertSql = """
            INSERT INTO characters (
                character_id, account_id, name, normalized_name, archetype, level, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """;
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = insertSql;
        insert.Parameters.AddWithValue(character.Id);
        insert.Parameters.AddWithValue(character.AccountId);
        insert.Parameters.AddWithValue(character.Name);
        insert.Parameters.AddWithValue(character.NormalizedName);
        insert.Parameters.AddWithValue((short)character.Archetype);
        insert.Parameters.AddWithValue(character.Level);
        insert.Parameters.AddWithValue(character.CreatedAt.UtcDateTime);

        try
        {
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AddCharacterOutcome.Added;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            return AddCharacterOutcome.NameAlreadyExists;
        }
    }

    public async ValueTask<GameCharacter?> SetDeletionScheduleAsync(
        Guid accountId,
        Guid characterId,
        DateTimeOffset? requestedAt,
        DateTimeOffset? deleteAfter,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE characters
            SET deletion_requested_at = $3, delete_after = $4
            WHERE account_id = $1 AND character_id = $2
            RETURNING character_id, account_id, name, normalized_name, archetype, level, created_at,
                      deletion_requested_at, delete_after
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(accountId);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(requestedAt?.UtcDateTime ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(deleteAfter?.UtcDateTime ?? (object)DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCharacter(reader) : null;
    }

    private static GameCharacter ReadCharacter(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            (CharacterArchetype)reader.GetInt16(4),
            reader.GetInt32(5),
            new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero),
            reader.IsDBNull(7) ? null : new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero),
            reader.IsDBNull(8) ? null : new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero));
}
