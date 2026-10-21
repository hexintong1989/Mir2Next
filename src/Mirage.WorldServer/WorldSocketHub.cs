using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Mirage.Domain.Characters;
using Mirage.Domain.World;
using Mirage.Shared.Contracts;

namespace Mirage.WorldServer;

public sealed class WorldSocketHub(
    WorldInstance world,
    WorldSimulation simulation,
    ICharacterWorldStateStore worldStates,
    ICharacterInventoryStore inventories,
    ICharacterStateStore characterStates,
    ILogger<WorldSocketHub> logger)
{
    private const int MaximumMessageBytes = 4096;
    private const int AoiRadius = 12;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, ClientConnection> _connections = [];
    private readonly SemaphoreSlim _visibilityGate = new(1, 1);

    public async Task AcceptAsync(
        HttpContext context,
        Guid? accountId,
        Guid characterId,
        GridPosition spawn,
        CharacterArchetype archetype,
        int? health,
        int? mana,
        int level,
        long experience,
        bool findAvailableSpawn,
        CancellationToken cancellationToken)
    {
        if (accountId is { } ownerId
            && !await worldStates.CanEnterWorldAsync(ownerId, characterId, cancellationToken))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var latest = await worldStates.LoadWorldStateAsync(characterId, cancellationToken);
        if (latest is not null && !string.Equals(latest.MapId, world.MapId, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        if (latest is not null)
        {
            spawn = new GridPosition(latest.PositionX, latest.PositionY);
            health = latest.Health ?? health;
            mana = latest.Mana ?? mana;
            level = latest.Level;
            experience = latest.Experience;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = new ClientConnection(characterId, socket);
        if (!_connections.TryAdd(characterId, connection))
        {
            await socket.CloseAsync(
                WebSocketCloseStatus.PolicyViolation,
                "Character already connected.",
                cancellationToken);
            return;
        }

        var join = findAvailableSpawn
            ? world.JoinAtAvailablePosition(characterId, spawn, archetype, level, experience)
            : world.Join(characterId, spawn);
        if (!join.IsSuccess && join.Failure != JoinFailure.AlreadyJoined)
        {
            _connections.TryRemove(characterId, out _);
            await socket.CloseAsync(
                WebSocketCloseStatus.PolicyViolation,
                $"Join rejected: {join.Failure}",
                cancellationToken);
            return;
        }

        try
        {
            var inventory = await inventories.LoadAsync(characterId, cancellationToken);
            world.ReplaceInventory(characterId, inventory?.Items ?? []);
            world.ReplaceEquipment(characterId, inventory?.Equipment ?? []);
            world.RestoreResources(characterId, health, mana, simulation.CurrentTick);
            await connection.SendAsync(new ServerEnvelope(
                "welcome",
                new
                {
                    protocolVersion = 1,
                    mapId = world.MapId,
                    player = ToPositionResponse(world.FindPlayer(characterId)!),
                    skills = SkillCatalog.ForArchetype(world.FindPlayer(characterId)!.Archetype)
                        .Select(skill => new
                        {
                            skillId = skill.SkillId,
                            range = skill.Range,
                            manaCost = skill.ManaCost,
                            cooldownTicks = skill.CooldownTicks,
                            minimumLevel = skill.MinimumLevel,
                            unlocked = world.FindPlayer(characterId)!.Level >= skill.MinimumLevel
                        })
                }), cancellationToken);
            await RefreshVisibilityAsync(null, cancellationToken);
            await SendMonsterSnapshotAsync(connection, cancellationToken);
            await SendDropSnapshotAsync(connection, cancellationToken);
            await SendInventorySnapshotAsync(connection, cancellationToken);
            await SendEquipmentSnapshotAsync(connection, cancellationToken);
            await ReceiveLoopAsync(connection, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal disconnect or server shutdown.
        }
        catch (WebSocketException exception)
        {
            logger.LogInformation(exception, "World socket disconnected for {CharacterId}.", characterId);
        }
        finally
        {
            if (_connections.TryGetValue(characterId, out var current)
                && ReferenceEquals(current, connection)
                && _connections.TryRemove(characterId, out _))
            {
                var departingPlayer = world.FindPlayer(characterId);
                var affectedPartyMembers = world.PartyFor(characterId)?.MemberCharacterIds ?? [];
                world.Leave(characterId);
                if (departingPlayer is not null)
                {
                    await TryPersistWorldStateAsync(departingPlayer, CancellationToken.None);
                    await TryPersistInventoryAsync(characterId, CancellationToken.None);
                }
                await BroadcastPartyStatesAsync(affectedPartyMembers, CancellationToken.None);
                await RefreshVisibilityAsync(null, CancellationToken.None);
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientConnection connection, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumMessageBytes];
        while (connection.Socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var count = 0;
            WebSocketReceiveResult result;
            do
            {
                result = await connection.Socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer, count, buffer.Length - count),
                    cancellationToken);
                count += result.Count;
                if (!result.EndOfMessage && count == buffer.Length)
                {
                    await connection.Socket.CloseAsync(
                        WebSocketCloseStatus.MessageTooBig,
                        "Message exceeds 4096 bytes.",
                        cancellationToken);
                    return;
                }
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await connection.Socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Disconnected.",
                    cancellationToken);
                return;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                await connection.SendAsync(
                    new ServerEnvelope("protocolError", new { code = "TextMessagesOnly" }),
                    cancellationToken);
                continue;
            }

            ClientEnvelope? message;
            try
            {
                message = JsonSerializer.Deserialize<ClientEnvelope>(buffer.AsSpan(0, count), JsonOptions);
            }
            catch (JsonException)
            {
                await connection.SendAsync(
                    new ServerEnvelope("protocolError", new { code = "InvalidJson" }),
                    cancellationToken);
                continue;
            }

            if (message is null)
            {
                await connection.SendAsync(
                    new ServerEnvelope("protocolError", new { code = "UnknownMessageType" }),
                    cancellationToken);
                continue;
            }

            if (string.Equals(message.Type, "move", StringComparison.Ordinal))
            {
                var movement = await simulation.QueueMoveAsync(
                    connection.CharacterId,
                    message.Sequence,
                    new GridPosition(message.X, message.Y),
                    cancellationToken);
                if (!movement.IsSuccess)
                {
                    await connection.SendAsync(
                        new ServerEnvelope("moveRejected", new
                        {
                            sequence = message.Sequence,
                            code = movement.Failure.ToString(),
                            player = ToPositionResponse(movement.Player)
                        }),
                        cancellationToken);
                    continue;
                }

                if (connection.ShouldCheckpoint(DateTimeOffset.UtcNow))
                {
                    await TryPersistWorldStateAsync(movement.Player, cancellationToken);
                }
                await RefreshVisibilityAsync(connection.CharacterId, cancellationToken);
                await SendMonsterSnapshotAsync(connection, cancellationToken);
                await SendDropSnapshotAsync(connection, cancellationToken);
                continue;
            }

            if (string.Equals(message.Type, "attack", StringComparison.Ordinal)
                && message.MonsterId != Guid.Empty)
            {
                var attack = await simulation.QueueAttackAsync(
                    connection.CharacterId,
                    message.Sequence,
                    message.MonsterId,
                    cancellationToken);
                if (!attack.IsSuccess)
                {
                    await connection.SendAsync(
                        new ServerEnvelope("attackRejected", new
                        {
                            sequence = message.Sequence,
                            code = attack.Failure.ToString()
                        }),
                        cancellationToken);
                    continue;
                }

                await BroadcastMonsterUpdateAsync(attack, cancellationToken);
                if (attack.Drop is not null)
                {
                    await BroadcastDropSnapshotAsync(cancellationToken);
                }
                if (attack.ExperienceGained > 0)
                {
                    await SendProgressionAsync(
                        connection,
                        attack.Player,
                        attack.ExperienceGained,
                        attack.LevelsGained,
                        cancellationToken);
                    await TryPersistWorldStateAsync(attack.Player, cancellationToken);
                }
                await RefreshVisibilityAsync(connection.CharacterId, cancellationToken);
                continue;
            }

            if (string.Equals(message.Type, "castSkill", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(message.SkillId)
                && message.MonsterId != Guid.Empty)
            {
                var skill = await simulation.QueueCastSkillAsync(
                    connection.CharacterId, message.Sequence, message.SkillId,
                    message.MonsterId, cancellationToken);
                if (!skill.IsSuccess)
                {
                    await connection.SendAsync(new ServerEnvelope("skillRejected", new
                    {
                        sequence = message.Sequence,
                        skillId = message.SkillId,
                        code = skill.Failure.ToString(),
                        player = ToPositionResponse(skill.Player)
                    }), cancellationToken);
                    continue;
                }

                await connection.SendAsync(new ServerEnvelope("skillResult", new
                {
                    sequence = message.Sequence,
                    skillId = skill.SkillId,
                    damage = skill.Damage,
                    defeated = skill.Defeated,
                    player = ToPositionResponse(skill.Player)
                }), cancellationToken);
                await BroadcastMonsterUpdateAsync(new AttackResult(AttackFailure.None,
                    skill.Player, skill.Monster, skill.Damage, skill.Defeated, skill.Drop), cancellationToken);
                if (skill.Drop is not null)
                {
                    await BroadcastDropSnapshotAsync(cancellationToken);
                }
                if (skill.ExperienceGained > 0)
                {
                    await SendProgressionAsync(
                        connection,
                        skill.Player,
                        skill.ExperienceGained,
                        skill.LevelsGained,
                        cancellationToken);
                    await TryPersistWorldStateAsync(skill.Player, cancellationToken);
                }
                await RefreshVisibilityAsync(connection.CharacterId, cancellationToken);
                continue;
            }

            if (string.Equals(message.Type, "chat", StringComparison.Ordinal))
            {
                var chat = await simulation.QueueChatAsync(
                    connection.CharacterId, message.Sequence, message.Text, cancellationToken);
                if (!chat.IsSuccess)
                {
                    await connection.SendAsync(new ServerEnvelope("chatRejected", new
                    {
                        sequence = message.Sequence,
                        code = chat.Failure.ToString()
                    }), cancellationToken);
                    continue;
                }

                await BroadcastChatAsync(chat, message.Sequence, cancellationToken);
                continue;
            }

            if (string.Equals(message.Type, "partyChat", StringComparison.Ordinal))
            {
                var chat = await simulation.QueuePartyChatAsync(
                    connection.CharacterId, message.Sequence, message.Text, cancellationToken);
                if (!chat.IsSuccess)
                {
                    await connection.SendAsync(new ServerEnvelope("partyChatRejected", new
                    {
                        sequence = message.Sequence,
                        code = chat.Failure.ToString()
                    }), cancellationToken);
                    continue;
                }

                await BroadcastPartyChatAsync(chat, message.Sequence, cancellationToken);
                continue;
            }

            if (string.Equals(message.Type, "useItem", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(message.ItemId))
            {
                var use = await simulation.QueueUseItemAsync(
                    connection.CharacterId, message.Sequence, message.ItemId, cancellationToken);
                if (!use.IsSuccess)
                {
                    await connection.SendAsync(new ServerEnvelope("itemUseRejected", new
                    {
                        sequence = message.Sequence,
                        code = use.Failure.ToString()
                    }), cancellationToken);
                    continue;
                }

                await TryPersistCombinedStateAsync(use.Player, cancellationToken);
                await connection.SendAsync(new ServerEnvelope("itemUsed", new
                {
                    sequence = message.Sequence,
                    itemId = message.ItemId,
                    player = ToPositionResponse(use.Player),
                    inventory = use.Inventory.Select(item =>
                        new InventoryItemResponse(item.ItemId, item.Quantity))
                }), cancellationToken);
                await RefreshVisibilityAsync(connection.CharacterId, cancellationToken);
                continue;
            }

            if (string.Equals(message.Type, "pickup", StringComparison.Ordinal)
                && message.DropId != Guid.Empty)
            {
                var pickup = await simulation.QueuePickupAsync(
                    connection.CharacterId,
                    message.Sequence,
                    message.DropId,
                    cancellationToken);
                if (!pickup.IsSuccess)
                {
                    await connection.SendAsync(
                        new ServerEnvelope("pickupRejected", new
                        {
                            sequence = message.Sequence,
                            code = pickup.Failure.ToString()
                        }),
                        cancellationToken);
                    continue;
                }

                await connection.SendAsync(
                    new ServerEnvelope("pickupResult", new
                    {
                        removedDropId = pickup.Drop!.DropId,
                        inventoryItem = new InventoryItemResponse(
                            pickup.InventoryItem!.ItemId,
                            pickup.InventoryItem.Quantity)
                    }),
                    cancellationToken);
                await TryPersistInventoryAsync(connection.CharacterId, cancellationToken);
                await BroadcastDropSnapshotAsync(cancellationToken);
                continue;
            }

            if (string.Equals(message.Type, "equip", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(message.ItemId))
            {
                var equipment = await simulation.QueueEquipAsync(
                    connection.CharacterId,
                    message.Sequence,
                    message.ItemId,
                    cancellationToken);
                await SendEquipmentResultAsync(connection, message.Sequence, equipment, cancellationToken);
                continue;
            }

            if (string.Equals(message.Type, "unequip", StringComparison.Ordinal)
                && Enum.TryParse<EquipmentSlot>(message.Slot, true, out var slot)
                && Enum.IsDefined(slot))
            {
                var equipment = await simulation.QueueUnequipAsync(
                    connection.CharacterId,
                    message.Sequence,
                    slot,
                    cancellationToken);
                await SendEquipmentResultAsync(connection, message.Sequence, equipment, cancellationToken);
                continue;
            }

            if (string.Equals(message.Type, "partyInvite", StringComparison.Ordinal)
                && message.TargetCharacterId != Guid.Empty)
            {
                var invite = await simulation.QueuePartyInviteAsync(
                    connection.CharacterId,
                    message.Sequence,
                    message.TargetCharacterId,
                    cancellationToken);
                if (!invite.IsSuccess)
                {
                    await SendPartyRejectedAsync(
                        connection, message.Sequence, "invite", invite.Failure, cancellationToken);
                    continue;
                }

                await connection.SendAsync(new ServerEnvelope("partyInviteSent", new
                {
                    sequence = message.Sequence,
                    targetCharacterId = invite.TargetCharacterId,
                    expiresAtTick = invite.ExpiresAtTick
                }), cancellationToken);
                if (_connections.TryGetValue(invite.TargetCharacterId, out var targetConnection))
                {
                    await targetConnection.SendAsync(new ServerEnvelope("partyInvite", new
                    {
                        inviterCharacterId = connection.CharacterId,
                        expiresAtTick = invite.ExpiresAtTick
                    }), cancellationToken);
                }
                continue;
            }

            if (string.Equals(message.Type, "partyAccept", StringComparison.Ordinal)
                && message.InviterCharacterId != Guid.Empty)
            {
                var accepted = await simulation.QueuePartyAcceptAsync(
                    connection.CharacterId,
                    message.Sequence,
                    message.InviterCharacterId,
                    cancellationToken);
                if (!accepted.IsSuccess)
                {
                    await SendPartyRejectedAsync(
                        connection, message.Sequence, "accept", accepted.Failure, cancellationToken);
                    continue;
                }

                await BroadcastPartyStatesAsync(accepted.AffectedCharacterIds, cancellationToken);
                continue;
            }

            if (string.Equals(message.Type, "partyLeave", StringComparison.Ordinal))
            {
                var left = await simulation.QueuePartyLeaveAsync(
                    connection.CharacterId,
                    message.Sequence,
                    cancellationToken);
                if (!left.IsSuccess)
                {
                    await SendPartyRejectedAsync(
                        connection, message.Sequence, "leave", left.Failure, cancellationToken);
                    continue;
                }

                await BroadcastPartyStatesAsync(left.AffectedCharacterIds, cancellationToken);
                continue;
            }

            await connection.SendAsync(
                new ServerEnvelope("protocolError", new { code = "UnknownMessageType" }),
                cancellationToken);
        }
    }

    private Task SendMonsterSnapshotAsync(
        ClientConnection connection,
        CancellationToken cancellationToken) =>
        connection.SendAsync(
            new ServerEnvelope(
                "monsterSnapshot",
                new
                {
                    monsters = world.MonstersVisibleTo(connection.CharacterId, AoiRadius)
                        .Select(ToMonsterResponse)
                }),
            cancellationToken);

    public async Task BroadcastMonsterSnapshotsAsync(CancellationToken cancellationToken)
    {
        await RefreshVisibilityAsync(null, cancellationToken);
        foreach (var connection in _connections.Values)
        {
            await SendMonsterSnapshotAsync(connection, cancellationToken);
            await SendDropSnapshotAsync(connection, cancellationToken);
        }
    }

    private Task SendDropSnapshotAsync(
        ClientConnection connection,
        CancellationToken cancellationToken) =>
        connection.SendAsync(
            new ServerEnvelope(
                "dropSnapshot",
                new
                {
                    drops = world.DropsVisibleTo(connection.CharacterId, AoiRadius)
                        .Select(ToDropResponse)
                }),
            cancellationToken);

    private Task SendInventorySnapshotAsync(
        ClientConnection connection,
        CancellationToken cancellationToken) =>
        connection.SendAsync(
            new ServerEnvelope(
                "inventorySnapshot",
                new
                {
                    items = world.InventorySnapshot(connection.CharacterId)
                        .Select(item => new InventoryItemResponse(item.ItemId, item.Quantity))
                }),
            cancellationToken);

    private Task SendEquipmentSnapshotAsync(
        ClientConnection connection,
        CancellationToken cancellationToken) =>
        connection.SendAsync(
            new ServerEnvelope(
                "equipmentSnapshot",
                new
                {
                    items = world.EquipmentSnapshot(connection.CharacterId)
                        .Select(ToEquippedItemResponse)
                }),
            cancellationToken);

    private async Task SendEquipmentResultAsync(
        ClientConnection connection,
        long sequence,
        EquipmentResult result,
        CancellationToken cancellationToken)
    {
        if (!result.IsSuccess)
        {
            await connection.SendAsync(
                new ServerEnvelope("equipmentRejected", new
                {
                    sequence,
                    code = result.Failure.ToString()
                }),
                cancellationToken);
            return;
        }

        await TryPersistInventoryAsync(connection.CharacterId, cancellationToken);
        await connection.SendAsync(
            new ServerEnvelope("equipmentChanged", new
            {
                sequence,
                player = ToPositionResponse(result.Player),
                inventory = result.Inventory.Select(item =>
                    new InventoryItemResponse(item.ItemId, item.Quantity)),
                equipment = result.Equipment.Select(ToEquippedItemResponse)
            }),
            cancellationToken);
        await RefreshVisibilityAsync(connection.CharacterId, cancellationToken);
    }

    private async Task BroadcastDropSnapshotAsync(CancellationToken cancellationToken)
    {
        foreach (var connection in _connections.Values)
        {
            await SendDropSnapshotAsync(connection, cancellationToken);
        }
    }

    private async Task BroadcastChatAsync(
        ChatResult chat,
        long sequence,
        CancellationToken cancellationToken)
    {
        var message = new ServerEnvelope("chatMessage", new
        {
            sequence,
            characterId = chat.Player.CharacterId,
            text = chat.Text,
            sentAt = DateTimeOffset.UtcNow
        });
        foreach (var connection in _connections.Values)
        {
            var recipient = world.FindPlayer(connection.CharacterId);
            if (recipient is null
                || recipient.Position.ChebyshevDistanceTo(chat.Player.Position) > AoiRadius)
            {
                continue;
            }

            await connection.SendAsync(message, cancellationToken);
        }
    }

    private async Task BroadcastPartyChatAsync(
        ChatResult chat,
        long sequence,
        CancellationToken cancellationToken)
    {
        var message = new ServerEnvelope("partyChatMessage", new
        {
            sequence,
            characterId = chat.Player.CharacterId,
            text = chat.Text,
            sentAt = DateTimeOffset.UtcNow
        });
        foreach (var characterId in chat.RecipientCharacterIds ?? [])
        {
            if (_connections.TryGetValue(characterId, out var connection))
            {
                await connection.SendAsync(message, cancellationToken);
            }
        }
    }

    private async Task BroadcastMonsterUpdateAsync(
        AttackResult attack,
        CancellationToken cancellationToken)
    {
        if (attack.Monster is null)
        {
            return;
        }

        foreach (var connection in _connections.Values)
        {
            var observer = world.FindPlayer(connection.CharacterId);
            if (observer is null
                || observer.Position.ChebyshevDistanceTo(attack.Monster.Position) > AoiRadius)
            {
                continue;
            }

            await connection.SendAsync(
                new ServerEnvelope("monsterUpdated", new
                {
                    monster = ToMonsterResponse(attack.Monster),
                    damage = attack.Damage,
                    defeated = attack.Defeated
                }),
                cancellationToken);
        }
    }

    private async Task TryPersistWorldStateAsync(
        WorldPlayer player,
        CancellationToken cancellationToken)
    {
        try
        {
            await worldStates.SaveWorldStateAsync(
                new CharacterWorldState(
                    player.CharacterId,
                    world.MapId,
                    player.Position.X,
                    player.Position.Y,
                    DateTimeOffset.UtcNow,
                    player.Health,
                    player.Mana,
                    player.Level,
                    player.Experience),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to persist world state for {CharacterId}.",
                player.CharacterId);
        }
    }

    private async Task TryPersistCombinedStateAsync(
        WorldPlayer player,
        CancellationToken cancellationToken)
    {
        var savedAt = DateTimeOffset.UtcNow;
        try
        {
            var saved = await characterStates.SaveInventoryAndWorldStateAsync(
                new CharacterInventorySnapshot(
                    player.CharacterId,
                    world.InventorySnapshot(player.CharacterId),
                    savedAt,
                    world.EquipmentSnapshot(player.CharacterId)),
                new CharacterWorldState(
                    player.CharacterId,
                    world.MapId,
                    player.Position.X,
                    player.Position.Y,
                    savedAt,
                    player.Health,
                    player.Mana,
                    player.Level,
                    player.Experience),
                cancellationToken);
            if (!saved)
            {
                logger.LogWarning(
                    "A newer inventory snapshot superseded item use for {CharacterId}.",
                    player.CharacterId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Failed to persist item use for {CharacterId}.", player.CharacterId);
        }
    }

    private async Task TryPersistInventoryAsync(
        Guid characterId,
        CancellationToken cancellationToken)
    {
        try
        {
            await inventories.SaveAsync(
                new CharacterInventorySnapshot(
                    characterId,
                    world.InventorySnapshot(characterId),
                    DateTimeOffset.UtcNow,
                    world.EquipmentSnapshot(characterId)),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to persist inventory for {CharacterId}.",
                characterId);
        }
    }

    private async Task RefreshVisibilityAsync(Guid? changedCharacterId, CancellationToken cancellationToken)
    {
        await _visibilityGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var connection in _connections.Values)
            {
                var visible = world.VisibleTo(connection.CharacterId, AoiRadius);
                var delta = connection.Visibility.Update(visible, changedCharacterId);
                if (!delta.HasChanges)
                {
                    continue;
                }

                await connection.SendAsync(new ServerEnvelope("visibilityDelta", new
                {
                    entered = delta.Entered.Select(ToPositionResponse),
                    updated = delta.Updated.Select(ToPositionResponse),
                    left = delta.Left
                }), cancellationToken);
            }
        }
        finally
        {
            _visibilityGate.Release();
        }
    }

    private static Task SendProgressionAsync(
        ClientConnection connection,
        WorldPlayer player,
        int experienceGained,
        int levelsGained,
        CancellationToken cancellationToken) =>
        connection.SendAsync(new ServerEnvelope("progression", new
        {
            experienceGained,
            levelsGained,
            player = ToPositionResponse(player)
        }), cancellationToken);

    private static Task SendPartyRejectedAsync(
        ClientConnection connection,
        long sequence,
        string operation,
        PartyFailure failure,
        CancellationToken cancellationToken) =>
        connection.SendAsync(new ServerEnvelope("partyRejected", new
        {
            sequence,
            operation,
            code = failure.ToString()
        }), cancellationToken);

    private async Task BroadcastPartyStatesAsync(
        IEnumerable<Guid> characterIds,
        CancellationToken cancellationToken)
    {
        foreach (var characterId in characterIds.Distinct())
        {
            if (!_connections.TryGetValue(characterId, out var connection))
            {
                continue;
            }

            await connection.SendAsync(new ServerEnvelope("partyUpdated", new
            {
                party = world.PartyFor(characterId)
            }), cancellationToken);
        }
    }

    private static PlayerPositionResponse ToPositionResponse(WorldPlayer player) =>
        new(
            player.CharacterId,
            player.Position.X,
            player.Position.Y,
            player.LastProcessedSequence,
            player.Health,
            player.MaximumHealth,
            player.IsAlive,
            player.InvulnerableUntilTick,
            player.Archetype,
            player.AttackPower,
            player.Defense,
            player.AttackCooldownTicks,
            player.Mana,
            player.MaximumMana,
            player.Level,
            player.Experience,
            player.Level < CharacterProgression.MaximumLevel
                ? CharacterProgression.ExperienceRequiredForLevel(player.Level + 1)
                : null);

    private static MonsterResponse ToMonsterResponse(WorldMonster monster) =>
        new(
            monster.MonsterId,
            monster.TemplateId,
            monster.Position.X,
            monster.Position.Y,
            monster.Level,
            monster.Health,
            monster.MaximumHealth);

    private static GroundDropResponse ToDropResponse(WorldDrop drop) =>
        new(
            drop.DropId,
            drop.ItemId,
            drop.Quantity,
            drop.Position.X,
            drop.Position.Y,
            drop.OwnerCharacterId,
            drop.OwnerProtectionUntilTick,
            drop.ExpiresAtTick);

    private static EquippedItemResponse ToEquippedItemResponse(EquippedItem item) =>
        new(item.Slot.ToString(), item.ItemId);

    private sealed record ClientEnvelope(
        string? Type,
        long Sequence,
        int X,
        int Y,
        Guid MonsterId,
        Guid DropId,
        string? ItemId,
        string? Slot,
        string? SkillId,
        string? Text,
        Guid TargetCharacterId,
        Guid InviterCharacterId);
    private sealed record ServerEnvelope(string Type, object Payload);

    private sealed class ClientConnection(Guid characterId, WebSocket socket)
    {
        private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(5);
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private DateTimeOffset _lastCheckpointAt = DateTimeOffset.MinValue;

        public Guid CharacterId { get; } = characterId;
        public WebSocket Socket { get; } = socket;
        public AoiVisibilityTracker Visibility { get; } = new();

        public bool ShouldCheckpoint(DateTimeOffset now)
        {
            if (now - _lastCheckpointAt < CheckpointInterval)
            {
                return false;
            }

            _lastCheckpointAt = now;
            return true;
        }

        public async Task SendAsync(ServerEnvelope message, CancellationToken cancellationToken)
        {
            if (Socket.State != WebSocketState.Open)
            {
                return;
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
            await _sendGate.WaitAsync(cancellationToken);
            try
            {
                if (Socket.State == WebSocketState.Open)
                {
                    await Socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
                }
            }
            finally
            {
                _sendGate.Release();
            }
        }
    }
}
