using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Mirage.Domain.World;
using Mirage.Shared.Contracts;

namespace Mirage.WorldServer;

public sealed class WorldSocketHub(
    WorldInstance world,
    WorldSimulation simulation,
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
                world.Leave(characterId);
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

            if (message is null || !string.Equals(message.Type, "move", StringComparison.Ordinal))
            {
                await connection.SendAsync(
                    new ServerEnvelope("protocolError", new { code = "UnknownMessageType" }),
                    cancellationToken);
                continue;
            }

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

            await RefreshVisibilityAsync(connection.CharacterId, cancellationToken);
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

    private sealed record ClientEnvelope(string? Type, long Sequence, int X, int Y);
    private sealed record ServerEnvelope(string Type, object Payload);

    private sealed class ClientConnection(Guid characterId, WebSocket socket)
    {
        private readonly SemaphoreSlim _sendGate = new(1, 1);

        public Guid CharacterId { get; } = characterId;
        public WebSocket Socket { get; } = socket;
        public AoiVisibilityTracker Visibility { get; } = new();

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
