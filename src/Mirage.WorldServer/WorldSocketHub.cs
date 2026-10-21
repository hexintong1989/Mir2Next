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
    ILogger<WorldSocketHub> logger)
{
    private const int MaximumMessageBytes = 4096;
    private const int AoiRadius = 12;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, ClientConnection> _connections = [];
    private readonly SemaphoreSlim _visibilityGate = new(1, 1);

    public async Task AcceptAsync(
        HttpContext context,
        Guid characterId,
        GridPosition spawn,
        bool findAvailableSpawn,
        CancellationToken cancellationToken)
    {
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
            ? world.JoinAtAvailablePosition(characterId, spawn)
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
            await connection.SendAsync(new ServerEnvelope(
                "welcome",
                new
                {
                    protocolVersion = 1,
                    mapId = world.MapId,
                    player = ToPositionResponse(world.FindPlayer(characterId)!)
                }), cancellationToken);
            await RefreshVisibilityAsync(null, cancellationToken);
            await SendMonsterSnapshotAsync(connection, cancellationToken);
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
                world.Leave(characterId);
                if (departingPlayer is not null)
                {
                    await TryPersistWorldStateAsync(departingPlayer, CancellationToken.None);
                }
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
                await RefreshVisibilityAsync(connection.CharacterId, cancellationToken);
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
        foreach (var connection in _connections.Values)
        {
            await SendMonsterSnapshotAsync(connection, cancellationToken);
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
                    DateTimeOffset.UtcNow),
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

    private static PlayerPositionResponse ToPositionResponse(WorldPlayer player) =>
        new(
            player.CharacterId,
            player.Position.X,
            player.Position.Y,
            player.LastProcessedSequence);

    private static MonsterResponse ToMonsterResponse(WorldMonster monster) =>
        new(
            monster.MonsterId,
            monster.TemplateId,
            monster.Position.X,
            monster.Position.Y,
            monster.Level,
            monster.Health,
            monster.MaximumHealth);

    private sealed record ClientEnvelope(
        string? Type,
        long Sequence,
        int X,
        int Y,
        Guid MonsterId);
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
