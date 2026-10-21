using System.Data;
using Mirage.Domain.Characters;
using Mirage.Shared.Contracts;
using Mirage.Domain.World;
using Npgsql;

namespace Mirage.Persistence.Postgres;

public sealed class PostgresCharacterRepository(NpgsqlDataSource dataSource) :
    ICharacterRepository,
    ICharacterStateStore
{
    public async ValueTask<bool> CanEnterWorldAsync(
        Guid accountId,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT deletion_requested_at IS NULL
            FROM characters
            WHERE account_id = $1 AND character_id = $2
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(accountId);
        command.Parameters.AddWithValue(characterId);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public async ValueTask<GameCharacter?> FindOwnedAsync(
        Guid accountId,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT character_id, account_id, name, normalized_name, archetype, level, created_at,
                   deletion_requested_at, delete_after, map_id, position_x, position_y,
                   world_state_saved_at, health, mana
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
                   deletion_requested_at, delete_after, map_id, position_x, position_y,
                   world_state_saved_at, health, mana
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
                      deletion_requested_at, delete_after, map_id, position_x, position_y,
                      world_state_saved_at, health, mana
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(accountId);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(requestedAt?.UtcDateTime ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(deleteAfter?.UtcDateTime ?? (object)DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCharacter(reader) : null;
    }

    public async ValueTask<bool> SaveWorldStateAsync(
        CharacterWorldState state,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE characters
            SET map_id = $2,
                position_x = $3,
                position_y = $4,
                world_state_saved_at = $5,
                health = COALESCE($6, health),
                mana = COALESCE($7, mana)
            WHERE character_id = $1
              AND (world_state_saved_at IS NULL OR world_state_saved_at <= $5)
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(state.CharacterId);
        command.Parameters.AddWithValue(state.MapId);
        command.Parameters.AddWithValue(state.PositionX);
        command.Parameters.AddWithValue(state.PositionY);
        command.Parameters.AddWithValue(state.SavedAt.UtcDateTime);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer,
            Value = (object?)state.Health ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer,
            Value = (object?)state.Mana ?? DBNull.Value });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async ValueTask<int> SaveWorldStatesAsync(
        IReadOnlyList<CharacterWorldState> states,
        CancellationToken cancellationToken)
    {
        var batch = CharacterWorldStateBatch.CollapseLatest(states);
        if (batch.Count == 0)
        {
            return 0;
        }

        const string sql = """
            WITH incoming (
                character_id, map_id, position_x, position_y, saved_at,
                health, has_health, mana, has_mana) AS (
                SELECT *
                FROM unnest(
                    $1::uuid[], $2::varchar[], $3::integer[], $4::integer[],
                    $5::timestamptz[], $6::integer[], $7::boolean[],
                    $8::integer[], $9::boolean[])
            )
            UPDATE characters AS c
            SET map_id = incoming.map_id,
                position_x = incoming.position_x,
                position_y = incoming.position_y,
                world_state_saved_at = incoming.saved_at,
                health = CASE WHEN incoming.has_health THEN incoming.health ELSE c.health END,
                mana = CASE WHEN incoming.has_mana THEN incoming.mana ELSE c.mana END
            FROM incoming
            WHERE c.character_id = incoming.character_id
              AND (c.world_state_saved_at IS NULL
                   OR c.world_state_saved_at <= incoming.saved_at)
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(
            NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid,
            batch.Select(state => state.CharacterId).ToArray());
        command.Parameters.AddWithValue(
            NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Varchar,
            batch.Select(state => state.MapId).ToArray());
        command.Parameters.AddWithValue(
            NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer,
            batch.Select(state => state.PositionX).ToArray());
        command.Parameters.AddWithValue(
            NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer,
            batch.Select(state => state.PositionY).ToArray());
        command.Parameters.AddWithValue(
            NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.TimestampTz,
            batch.Select(state => state.SavedAt.UtcDateTime).ToArray());
        command.Parameters.AddWithValue(
            NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer,
            batch.Select(state => state.Health ?? 0).ToArray());
        command.Parameters.AddWithValue(
            NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Boolean,
            batch.Select(state => state.Health.HasValue).ToArray());
        command.Parameters.AddWithValue(
            NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer,
            batch.Select(state => state.Mana ?? 0).ToArray());
        command.Parameters.AddWithValue(
            NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Boolean,
            batch.Select(state => state.Mana.HasValue).ToArray());
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<CharacterWorldState?> LoadWorldStateAsync(
        Guid characterId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT map_id, position_x, position_y, world_state_saved_at, health, mana
            FROM characters
            WHERE character_id = $1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(3))
        {
            return null;
        }

        return new CharacterWorldState(characterId, reader.GetString(0), reader.GetInt32(1),
            reader.GetInt32(2), new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetInt32(5));
    }

    public async ValueTask<IReadOnlyList<CharacterDeletionAudit>> PurgeDueDeletionsAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var due = new List<CharacterDeletionAudit>();

        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT character_id, account_id, name, deletion_requested_at
                FROM characters
                WHERE delete_after <= $1
                ORDER BY delete_after, character_id
                LIMIT $2
                FOR UPDATE SKIP LOCKED
                """;
            select.Parameters.AddWithValue(now.UtcDateTime);
            select.Parameters.AddWithValue(maximumCount);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                due.Add(new CharacterDeletionAudit(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero),
                    now));
            }
        }

        foreach (var audit in due)
        {
            await using var insertAudit = connection.CreateCommand();
            insertAudit.Transaction = transaction;
            insertAudit.CommandText = """
                INSERT INTO character_deletion_audit (
                    character_id, account_id, name, deletion_requested_at, deleted_at)
                VALUES ($1, $2, $3, $4, $5)
                ON CONFLICT (character_id) DO NOTHING
                """;
            insertAudit.Parameters.AddWithValue(audit.CharacterId);
            insertAudit.Parameters.AddWithValue(audit.AccountId);
            insertAudit.Parameters.AddWithValue(audit.Name);
            insertAudit.Parameters.AddWithValue(audit.RequestedAt.UtcDateTime);
            insertAudit.Parameters.AddWithValue(audit.DeletedAt.UtcDateTime);
            await insertAudit.ExecuteNonQueryAsync(cancellationToken);
        }

        if (due.Count > 0)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM characters WHERE character_id = ANY($1)";
            delete.Parameters.AddWithValue(due.Select(value => value.CharacterId).ToArray());
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return due;
    }

    public async ValueTask<CharacterInventorySnapshot?> LoadAsync(
        Guid characterId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT state.saved_at, item.item_id, item.quantity
            FROM character_inventory_state state
            LEFT JOIN character_inventory_items item
              ON item.character_id = state.character_id
            WHERE state.character_id = $1
            ORDER BY item.item_id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        DateTimeOffset? savedAt = null;
        var items = new List<InventoryItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            savedAt ??= new DateTimeOffset(reader.GetDateTime(0), TimeSpan.Zero);
            if (!reader.IsDBNull(1))
            {
                items.Add(new InventoryItem(reader.GetString(1), reader.GetInt32(2)));
            }
        }

        await reader.DisposeAsync();

        const string equipmentSql = """
            SELECT slot, item_id
            FROM character_equipment
            WHERE character_id = $1
            ORDER BY slot
            """;
        await using var equipmentCommand = dataSource.CreateCommand(equipmentSql);
        equipmentCommand.Parameters.AddWithValue(characterId);
        await using var equipmentReader = await equipmentCommand.ExecuteReaderAsync(cancellationToken);
        var equipment = new List<EquippedItem>();
        while (await equipmentReader.ReadAsync(cancellationToken))
        {
            equipment.Add(new EquippedItem(
                (EquipmentSlot)equipmentReader.GetInt16(0),
                equipmentReader.GetString(1)));
        }

        return savedAt is null
            ? null
            : new CharacterInventorySnapshot(characterId, items, savedAt.Value, equipment);
    }

    public async ValueTask<bool> SaveAsync(
        CharacterInventorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await TryAdvanceInventoryVersionAsync(connection, transaction, snapshot, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await ReplaceInventoryRowsAsync(connection, transaction, snapshot, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async ValueTask<bool> SaveInventoryAndWorldStateAsync(
        CharacterInventorySnapshot inventory,
        CharacterWorldState worldState,
        CancellationToken cancellationToken)
    {
        if (inventory.CharacterId != worldState.CharacterId
            || inventory.SavedAt != worldState.SavedAt)
        {
            throw new ArgumentException("Combined snapshots must share character and version.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var characterLock = connection.CreateCommand())
        {
            characterLock.Transaction = transaction;
            characterLock.CommandText = "SELECT character_id FROM characters WHERE character_id = $1 FOR UPDATE";
            characterLock.Parameters.AddWithValue(worldState.CharacterId);
            if (await characterLock.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        if (!await TryAdvanceInventoryVersionAsync(connection, transaction, inventory, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await using (var updateWorld = connection.CreateCommand())
        {
            updateWorld.Transaction = transaction;
            updateWorld.CommandText = """
                UPDATE characters
                SET map_id = $2,
                    position_x = $3,
                    position_y = $4,
                    world_state_saved_at = $5,
                    health = COALESCE($6, health),
                    mana = COALESCE($7, mana)
                WHERE character_id = $1
                  AND (world_state_saved_at IS NULL OR world_state_saved_at <= $5)
                """;
            updateWorld.Parameters.AddWithValue(worldState.CharacterId);
            updateWorld.Parameters.AddWithValue(worldState.MapId);
            updateWorld.Parameters.AddWithValue(worldState.PositionX);
            updateWorld.Parameters.AddWithValue(worldState.PositionY);
            updateWorld.Parameters.AddWithValue(worldState.SavedAt.UtcDateTime);
            updateWorld.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer,
                Value = (object?)worldState.Health ?? DBNull.Value });
            updateWorld.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer,
                Value = (object?)worldState.Mana ?? DBNull.Value });
            await updateWorld.ExecuteNonQueryAsync(cancellationToken);
        }

        await ReplaceInventoryRowsAsync(connection, transaction, inventory, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<bool> TryAdvanceInventoryVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CharacterInventorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var version = connection.CreateCommand();
        version.Transaction = transaction;
        version.CommandText = """
            INSERT INTO character_inventory_state (character_id, saved_at)
            VALUES ($1, $2)
            ON CONFLICT (character_id) DO UPDATE
            SET saved_at = EXCLUDED.saved_at
            WHERE character_inventory_state.saved_at <= EXCLUDED.saved_at
            RETURNING character_id
            """;
        version.Parameters.AddWithValue(snapshot.CharacterId);
        version.Parameters.AddWithValue(snapshot.SavedAt.UtcDateTime);
        return await version.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task ReplaceInventoryRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CharacterInventorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM character_inventory_items WHERE character_id = $1";
            delete.Parameters.AddWithValue(snapshot.CharacterId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var item in snapshot.Items)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO character_inventory_items (character_id, item_id, quantity)
                VALUES ($1, $2, $3)
                """;
            insert.Parameters.AddWithValue(snapshot.CharacterId);
            insert.Parameters.AddWithValue(item.ItemId);
            insert.Parameters.AddWithValue(item.Quantity);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteEquipment = connection.CreateCommand())
        {
            deleteEquipment.Transaction = transaction;
            deleteEquipment.CommandText = "DELETE FROM character_equipment WHERE character_id = $1";
            deleteEquipment.Parameters.AddWithValue(snapshot.CharacterId);
            await deleteEquipment.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var item in snapshot.Equipment ?? [])
        {
            await using var insertEquipment = connection.CreateCommand();
            insertEquipment.Transaction = transaction;
            insertEquipment.CommandText = """
                INSERT INTO character_equipment (character_id, slot, item_id)
                VALUES ($1, $2, $3)
                """;
            insertEquipment.Parameters.AddWithValue(snapshot.CharacterId);
            insertEquipment.Parameters.AddWithValue((short)item.Slot);
            insertEquipment.Parameters.AddWithValue(item.ItemId);
            await insertEquipment.ExecuteNonQueryAsync(cancellationToken);
        }

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
            reader.IsDBNull(8) ? null : new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero),
            reader.GetString(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.IsDBNull(12) ? null : new DateTimeOffset(reader.GetDateTime(12), TimeSpan.Zero),
            reader.IsDBNull(13) ? null : reader.GetInt32(13),
            reader.IsDBNull(14) ? null : reader.GetInt32(14));
}
