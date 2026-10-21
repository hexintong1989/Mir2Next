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

public enum AttackFailure
{
    None,
    PlayerNotFound,
    MonsterNotFound,
    StaleSequence,
    OutOfRange,
    Cooldown
}

public sealed record WorldPlayer(
    Guid CharacterId,
    GridPosition Position,
    long LastProcessedSequence,
    long LastAttackTick = -1000);

public sealed record WorldMonster(
    Guid MonsterId,
    string TemplateId,
    GridPosition Position,
    int Level,
    int Health,
    int MaximumHealth);

public readonly record struct JoinResult(JoinFailure Failure, WorldPlayer? Player)
{
    public bool IsSuccess => Failure == JoinFailure.None;
}

public readonly record struct MoveResult(MoveFailure Failure, WorldPlayer Player)
{
    public bool IsSuccess => Failure == MoveFailure.None;
}

public readonly record struct AttackResult(
    AttackFailure Failure,
    WorldPlayer Player,
    WorldMonster? Monster,
    int Damage,
    bool Defeated)
{
    public bool IsSuccess => Failure == AttackFailure.None;
}

public sealed class WorldInstance
{
    public const int BasicAttackDamage = 10;
    public const int BasicAttackCooldownTicks = 10;
    public const int MonsterBehaviorIntervalTicks = 20;
    public const int MonsterRespawnTicks = 100;
    public const int MonsterAggroRadius = 8;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, WorldPlayer> _players = [];
    private readonly Dictionary<Guid, WorldMonster> _monsters = [];
    private readonly Dictionary<Guid, MonsterSpawn> _monsterSpawns = [];
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

    public int MonsterCount
    {
        get
        {
            lock (_gate)
            {
                return _monsters.Count;
            }
        }
    }

    public bool SpawnMonster(
        Guid monsterId,
        string templateId,
        GridPosition position,
        int level,
        int maximumHealth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(level);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumHealth);
        lock (_gate)
        {
            if (_monsterSpawns.ContainsKey(monsterId)
                || !Contains(position)
                || _occupiedPositions.Contains(position))
            {
                return false;
            }

            _monsters.Add(
                monsterId,
                new WorldMonster(
                    monsterId,
                    templateId,
                    position,
                    level,
                    maximumHealth,
                    maximumHealth));
            _monsterSpawns.Add(
                monsterId,
                new MonsterSpawn(
                    monsterId,
                    templateId,
                    position,
                    level,
                    maximumHealth,
                    null));
            _occupiedPositions.Add(position);
            return true;
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

    public JoinResult JoinAtAvailablePosition(Guid characterId, GridPosition preferredSpawn)
    {
        lock (_gate)
        {
            if (_players.TryGetValue(characterId, out var existing))
            {
                return new JoinResult(JoinFailure.AlreadyJoined, existing);
            }

            for (var offset = 0; offset < Width * Height; offset++)
            {
                var index = ((preferredSpawn.Y * Width + preferredSpawn.X) + offset) % (Width * Height);
                var candidate = new GridPosition(index % Width, index / Width);
                if (_occupiedPositions.Contains(candidate))
                {
                    continue;
                }

                var player = new WorldPlayer(characterId, candidate, 0);
                _players.Add(characterId, player);
                _occupiedPositions.Add(candidate);
                return new JoinResult(JoinFailure.None, player);
            }

            return new JoinResult(JoinFailure.Occupied, null);
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

    public AttackResult Attack(
        Guid characterId,
        long sequence,
        Guid monsterId,
        long currentTick)
    {
        lock (_gate)
        {
            if (!_players.TryGetValue(characterId, out var player))
            {
                return new AttackResult(
                    AttackFailure.PlayerNotFound,
                    new WorldPlayer(characterId, default, 0),
                    null,
                    0,
                    false);
            }

            if (sequence <= player.LastProcessedSequence)
            {
                return new AttackResult(AttackFailure.StaleSequence, player, null, 0, false);
            }

            player = player with { LastProcessedSequence = sequence };
            _players[characterId] = player;
            if (!_monsters.TryGetValue(monsterId, out var monster))
            {
                return new AttackResult(AttackFailure.MonsterNotFound, player, null, 0, false);
            }

            if (player.Position.ChebyshevDistanceTo(monster.Position) > 1)
            {
                return new AttackResult(AttackFailure.OutOfRange, player, monster, 0, false);
            }

            if (currentTick - player.LastAttackTick < BasicAttackCooldownTicks)
            {
                return new AttackResult(AttackFailure.Cooldown, player, monster, 0, false);
            }

            player = player with { LastAttackTick = currentTick };
            _players[characterId] = player;
            var damage = Math.Min(BasicAttackDamage, monster.Health);
            var health = monster.Health - damage;
            monster = monster with { Health = health };
            var defeated = health == 0;
            if (defeated)
            {
                _monsters.Remove(monsterId);
                _occupiedPositions.Remove(monster.Position);
                _monsterSpawns[monsterId] = _monsterSpawns[monsterId] with
                {
                    RespawnAtTick = currentTick + MonsterRespawnTicks
                };
            }
            else
            {
                _monsters[monsterId] = monster;
            }

            return new AttackResult(
                AttackFailure.None,
                player,
                monster,
                damage,
                defeated);
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

    public IReadOnlyList<WorldMonster> MonsterSnapshot()
    {
        lock (_gate)
        {
            return _monsters.Values.OrderBy(monster => monster.MonsterId).ToArray();
        }
    }

    public int Advance(long currentTick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(currentTick);
        lock (_gate)
        {
            var changes = 0;
            foreach (var spawn in _monsterSpawns.Values
                         .Where(value => value.RespawnAtTick <= currentTick)
                         .OrderBy(value => value.MonsterId)
                         .ToArray())
            {
                if (_occupiedPositions.Contains(spawn.Position))
                {
                    continue;
                }

                _monsters[spawn.MonsterId] = new WorldMonster(
                    spawn.MonsterId,
                    spawn.TemplateId,
                    spawn.Position,
                    spawn.Level,
                    spawn.MaximumHealth,
                    spawn.MaximumHealth);
                _occupiedPositions.Add(spawn.Position);
                _monsterSpawns[spawn.MonsterId] = spawn with { RespawnAtTick = null };
                changes++;
            }

            if (currentTick % MonsterBehaviorIntervalTicks != 0 || _players.Count == 0)
            {
                return changes;
            }

            foreach (var monster in _monsters.Values.OrderBy(value => value.MonsterId).ToArray())
            {
                var target = _players.Values
                    .Select(player => new
                    {
                        Player = player,
                        Distance = monster.Position.ChebyshevDistanceTo(player.Position)
                    })
                    .Where(value => value.Distance is > 1 and <= MonsterAggroRadius)
                    .OrderBy(value => value.Distance)
                    .ThenBy(value => value.Player.CharacterId)
                    .FirstOrDefault();
                if (target is null)
                {
                    continue;
                }

                var candidate = new GridPosition(
                    monster.Position.X + Math.Sign(target.Player.Position.X - monster.Position.X),
                    monster.Position.Y + Math.Sign(target.Player.Position.Y - monster.Position.Y));
                if (!Contains(candidate) || _occupiedPositions.Contains(candidate))
                {
                    continue;
                }

                _occupiedPositions.Remove(monster.Position);
                _occupiedPositions.Add(candidate);
                _monsters[monster.MonsterId] = monster with { Position = candidate };
                changes++;
            }

            return changes;
        }
    }

    public IReadOnlyList<WorldMonster> MonstersVisibleTo(Guid characterId, int radius)
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

            return _monsters.Values
                .Where(monster => observer.Position.ChebyshevDistanceTo(monster.Position) <= radius)
                .OrderBy(monster => monster.MonsterId)
                .ToArray();
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

    private sealed record MonsterSpawn(
        Guid MonsterId,
        string TemplateId,
        GridPosition Position,
        int Level,
        int MaximumHealth,
        long? RespawnAtTick);
}
