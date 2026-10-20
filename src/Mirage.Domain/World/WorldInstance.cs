namespace Mirage.Domain.World;

public enum JoinFailure
{
    None,
    AlreadyJoined,
    OutOfBounds,
    Occupied
}

public enum MoveFailure
{
    None,
    PlayerNotFound,
    StaleSequence,
    OutOfBounds,
    InvalidStep,
    Occupied
}

public sealed record WorldPlayer(
    Guid CharacterId,
    GridPosition Position,
    long LastProcessedSequence);

public readonly record struct JoinResult(JoinFailure Failure, WorldPlayer? Player)
{
    public bool IsSuccess => Failure == JoinFailure.None;
}

public readonly record struct MoveResult(MoveFailure Failure, WorldPlayer Player)
{
    public bool IsSuccess => Failure == MoveFailure.None;
}

public sealed class WorldInstance
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, WorldPlayer> _players = [];
    private readonly HashSet<GridPosition> _occupiedPositions = [];

    public WorldInstance(string mapId, int width, int height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Map dimensions must be positive.");
        }

        MapId = mapId;
        Width = width;
        Height = height;
    }

    public string MapId { get; }
    public int Width { get; }
    public int Height { get; }

    public int PlayerCount
    {
        get
        {
            lock (_gate)
            {
                return _players.Count;
            }
        }
    }

    public JoinResult Join(Guid characterId, GridPosition spawn)
    {
        lock (_gate)
        {
            if (_players.TryGetValue(characterId, out var existing))
            {
                return new JoinResult(JoinFailure.AlreadyJoined, existing);
            }

            if (!Contains(spawn))
            {
                return new JoinResult(JoinFailure.OutOfBounds, null);
            }

            if (_occupiedPositions.Contains(spawn))
            {
                return new JoinResult(JoinFailure.Occupied, null);
            }

            var player = new WorldPlayer(characterId, spawn, 0);
            _players.Add(characterId, player);
            _occupiedPositions.Add(spawn);
            return new JoinResult(JoinFailure.None, player);
        }
    }

    public MoveResult Move(Guid characterId, long sequence, GridPosition target)
    {
        lock (_gate)
        {
            if (!_players.TryGetValue(characterId, out var player))
            {
                return new MoveResult(
                    MoveFailure.PlayerNotFound,
                    new WorldPlayer(characterId, target, 0));
            }

            if (sequence <= player.LastProcessedSequence)
            {
                return new MoveResult(MoveFailure.StaleSequence, player);
            }

            player = player with { LastProcessedSequence = sequence };
            _players[characterId] = player;

            if (!Contains(target))
            {
                return new MoveResult(MoveFailure.OutOfBounds, player);
            }

            if (player.Position.ChebyshevDistanceTo(target) != 1)
            {
                return new MoveResult(MoveFailure.InvalidStep, player);
            }

            if (_occupiedPositions.Contains(target))
            {
                return new MoveResult(MoveFailure.Occupied, player);
            }

            _occupiedPositions.Remove(player.Position);
            _occupiedPositions.Add(target);
            player = player with { Position = target };
            _players[characterId] = player;
            return new MoveResult(MoveFailure.None, player);
        }
    }

    public bool Leave(Guid characterId)
    {
        lock (_gate)
        {
            if (!_players.Remove(characterId, out var player))
            {
                return false;
            }

            _occupiedPositions.Remove(player.Position);
            return true;
        }
    }

    public WorldPlayer? FindPlayer(Guid characterId)
    {
        lock (_gate)
        {
            return _players.GetValueOrDefault(characterId);
        }
    }

    public IReadOnlyList<WorldPlayer> Snapshot()
    {
        lock (_gate)
        {
            return _players.Values.OrderBy(player => player.CharacterId).ToArray();
        }
    }

    public IReadOnlyList<WorldPlayer> VisibleTo(Guid characterId, int radius)
    {
        if (radius is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "AOI radius must be between 1 and 64.");
        }

        lock (_gate)
        {
            if (!_players.TryGetValue(characterId, out var observer))
            {
                return [];
            }

            return _players.Values
                .Where(player => observer.Position.ChebyshevDistanceTo(player.Position) <= radius)
                .OrderBy(player => player.CharacterId)
                .ToArray();
        }
    }

    private bool Contains(GridPosition position) =>
        position.X >= 0 && position.X < Width && position.Y >= 0 && position.Y < Height;
}
