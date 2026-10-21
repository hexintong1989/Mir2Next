using Mirage.Shared.Contracts;

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
    Occupied,
    PlayerDead
}

public enum AttackFailure
{
    None,
    PlayerNotFound,
    MonsterNotFound,
    StaleSequence,
    OutOfRange,
    Cooldown,
    PlayerDead
}

public enum SkillFailure
{
    None,
    PlayerNotFound,
    PlayerDead,
    StaleSequence,
    UnknownSkill,
    WrongArchetype,
    LevelTooLow,
    MonsterNotFound,
    OutOfRange,
    InsufficientMana,
    Cooldown
}

public enum ChatFailure
{
    None,
    PlayerNotFound,
    PlayerDead,
    StaleSequence,
    InvalidText,
    Cooldown,
    NotInParty
}

public enum UseItemFailure
{
    None,
    PlayerNotFound,
    PlayerDead,
    StaleSequence,
    ItemNotConsumable,
    ItemNotFound,
    NoEffect
}

public enum PickupFailure
{
    None,
    PlayerNotFound,
    DropNotFound,
    StaleSequence,
    OutOfRange,
    Protected,
    InventoryFull,
    PlayerDead
}

public sealed record WorldPlayer(
    Guid CharacterId,
    GridPosition Position,
    long LastProcessedSequence,
    long LastAttackTick = -1000,
    int Health = 100,
    int MaximumHealth = 100,
    long? RespawnAtTick = null,
    long InvulnerableUntilTick = 0,
    CharacterArchetype Archetype = CharacterArchetype.Warrior,
    int AttackPower = 12,
    int Defense = 3,
    int AttackCooldownTicks = 10,
    int Mana = 30,
    int MaximumMana = 30,
    int Level = CharacterProgression.MinimumLevel,
    long Experience = 0)
{
    public bool IsAlive => Health > 0;
}

public sealed record WorldMonster(
    Guid MonsterId,
    string TemplateId,
    GridPosition Position,
    int Level,
    int Health,
    int MaximumHealth,
    long LastAttackTick = -1000);

public sealed record WorldDrop(
    Guid DropId,
    string ItemId,
    int Quantity,
    GridPosition Position,
    Guid OwnerCharacterId,
    long OwnerProtectionUntilTick,
    long ExpiresAtTick);

public sealed record InventoryItem(string ItemId, int Quantity);

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
    bool Defeated,
    WorldDrop? Drop,
    int ExperienceGained = 0,
    int LevelsGained = 0,
    IReadOnlyList<ProgressionReward>? ProgressionRewards = null)
{
    public bool IsSuccess => Failure == AttackFailure.None;
}

public readonly record struct SkillResult(
    SkillFailure Failure,
    string SkillId,
    WorldPlayer Player,
    WorldMonster? Monster,
    int Damage,
    bool Defeated,
    WorldDrop? Drop,
    int ExperienceGained = 0,
    int LevelsGained = 0,
    IReadOnlyList<ProgressionReward>? ProgressionRewards = null)
{
    public bool IsSuccess => Failure == SkillFailure.None;
}

public readonly record struct ChatResult(
    ChatFailure Failure,
    WorldPlayer Player,
    string? Text,
    IReadOnlyList<Guid>? RecipientCharacterIds = null)
{
    public bool IsSuccess => Failure == ChatFailure.None;
}

public readonly record struct UseItemResult(
    UseItemFailure Failure,
    WorldPlayer Player,
    IReadOnlyList<InventoryItem> Inventory)
{
    public bool IsSuccess => Failure == UseItemFailure.None;
}

public readonly record struct PickupResult(
    PickupFailure Failure,
    WorldPlayer Player,
    WorldDrop? Drop,
    InventoryItem? InventoryItem)
{
    public bool IsSuccess => Failure == PickupFailure.None;
}

public sealed class WorldInstance
{
    public const int BasicAttackDamage = 10;
    public const int BasicAttackCooldownTicks = 10;
    public const int MonsterBehaviorIntervalTicks = 20;
    public const int MonsterRespawnTicks = 100;
    public const int MonsterAggroRadius = 8;
    public const int DropOwnerProtectionTicks = 200;
    public const int DropLifetimeTicks = 1200;
    public const int InventorySlotLimit = 20;
    public const int MonsterAttackDamage = 8;
    public const int MonsterAttackCooldownTicks = 20;
    public const int PlayerRespawnTicks = 60;
    public const int PlayerRespawnProtectionTicks = 40;
    public const int ManaRegenerationIntervalTicks = 20;
    public const int ChatCooldownTicks = 20;
    public const int MaximumChatLength = 120;
    public const int MaximumPartyMembers = 5;
    public const int PartyInviteLifetimeTicks = 600;
    public const int PartyExperienceRadius = 12;
    public const int PartyExperienceBonusPercentPerAdditionalMember = 10;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, WorldPlayer> _players = [];
    private readonly Dictionary<Guid, WorldMonster> _monsters = [];
    private readonly Dictionary<Guid, MonsterSpawn> _monsterSpawns = [];
    private readonly Dictionary<Guid, WorldDrop> _groundDrops = [];
    private readonly Dictionary<Guid, Dictionary<string, int>> _inventories = [];
    private readonly Dictionary<Guid, Dictionary<EquipmentSlot, string>> _equipment = [];
    private readonly Dictionary<(Guid CharacterId, string SkillId), long> _skillLastCastTicks = [];
    private readonly Dictionary<Guid, long> _chatLastSentTicks = [];
    private readonly Dictionary<Guid, GridPosition> _playerSpawns = [];
    private readonly Dictionary<Guid, WorldPartyState> _parties = [];
    private readonly Dictionary<Guid, Guid> _partyByCharacter = [];
    private readonly Dictionary<Guid, PartyInvitation> _partyInvitations = [];
    private readonly HashSet<GridPosition> _occupiedPositions = [];
    private long _nextDropSequence;

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
        int maximumHealth,
        string dropItemId = "gold")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(level);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumHealth);
        ArgumentException.ThrowIfNullOrWhiteSpace(dropItemId);
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
                    dropItemId,
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

            var player = CreatePlayer(characterId, spawn, CharacterArchetype.Warrior);
            _players.Add(characterId, player);
            _playerSpawns[characterId] = spawn;
            _inventories.TryAdd(characterId, new Dictionary<string, int>(StringComparer.Ordinal));
            _equipment.TryAdd(characterId, []);
            _occupiedPositions.Add(spawn);
            return new JoinResult(JoinFailure.None, player);
        }
    }

    public JoinResult JoinAtAvailablePosition(
        Guid characterId,
        GridPosition preferredSpawn,
        CharacterArchetype archetype = CharacterArchetype.Warrior,
        int level = CharacterProgression.MinimumLevel,
        long experience = 0)
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

                var player = CreatePlayer(characterId, candidate, archetype, level, experience);
                _players.Add(characterId, player);
                _playerSpawns[characterId] = candidate;
                _inventories.TryAdd(characterId, new Dictionary<string, int>(StringComparer.Ordinal));
                _equipment.TryAdd(characterId, []);
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

            if (!player.IsAlive)
            {
                return new MoveResult(MoveFailure.PlayerDead, player);
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
                    false,
                    null);
            }

            if (sequence <= player.LastProcessedSequence)
            {
                return new AttackResult(AttackFailure.StaleSequence, player, null, 0, false, null);
            }

            if (!player.IsAlive)
            {
                return new AttackResult(AttackFailure.PlayerDead, player, null, 0, false, null);
            }

            player = player with { LastProcessedSequence = sequence };
            _players[characterId] = player;
            if (!_monsters.TryGetValue(monsterId, out var monster))
            {
                return new AttackResult(AttackFailure.MonsterNotFound, player, null, 0, false, null);
            }

            if (player.Position.ChebyshevDistanceTo(monster.Position) > 1)
            {
                return new AttackResult(AttackFailure.OutOfRange, player, monster, 0, false, null);
            }

            if (currentTick - player.LastAttackTick < player.AttackCooldownTicks)
            {
                return new AttackResult(AttackFailure.Cooldown, player, monster, 0, false, null);
            }

            player = player with { LastAttackTick = currentTick };
            _players[characterId] = player;
            var damage = Math.Min(player.AttackPower, monster.Health);
            var health = monster.Health - damage;
            monster = monster with { Health = health };
            var defeated = health == 0;
            WorldDrop? drop = null;
            var experienceGained = 0;
            var levelsGained = 0;
            IReadOnlyList<ProgressionReward>? progressionRewards = null;
            if (defeated)
            {
                _monsters.Remove(monsterId);
                _occupiedPositions.Remove(monster.Position);
                _monsterSpawns[monsterId] = _monsterSpawns[monsterId] with
                {
                    RespawnAtTick = currentTick + MonsterRespawnTicks
                };
                drop = CreateDrop(
                    _monsterSpawns[monsterId].DropItemId,
                    monster.Position,
                    characterId,
                    currentTick);
                progressionRewards = AwardDefeatExperience(player, monster);
                var killerReward = progressionRewards.Single(value => value.CharacterId == characterId);
                player = killerReward.Player;
                experienceGained = killerReward.ExperienceGained;
                levelsGained = killerReward.LevelsGained;
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
                defeated,
                drop,
                experienceGained,
                levelsGained,
                progressionRewards);
        }
    }

    public SkillResult CastSkill(
        Guid characterId,
        long sequence,
        string skillId,
        Guid monsterId,
        long currentTick)
    {
        lock (_gate)
        {
            if (!_players.TryGetValue(characterId, out var player))
            {
                return new SkillResult(SkillFailure.PlayerNotFound, skillId,
                    new WorldPlayer(characterId, default, 0), null, 0, false, null);
            }

            SkillResult Reject(SkillFailure failure, WorldMonster? target = null) =>
                new(failure, skillId, player, target, 0, false, null);

            if (sequence <= player.LastProcessedSequence)
            {
                return Reject(SkillFailure.StaleSequence);
            }

            if (!player.IsAlive)
            {
                return Reject(SkillFailure.PlayerDead);
            }

            player = player with { LastProcessedSequence = sequence };
            _players[characterId] = player;
            if (!SkillCatalog.TryGet(skillId, out var skill))
            {
                return Reject(SkillFailure.UnknownSkill);
            }

            if (skill.Archetype != player.Archetype)
            {
                return Reject(SkillFailure.WrongArchetype);
            }

            if (player.Level < skill.MinimumLevel)
            {
                return Reject(SkillFailure.LevelTooLow);
            }

            if (!_monsters.TryGetValue(monsterId, out var monster))
            {
                return Reject(SkillFailure.MonsterNotFound);
            }

            if (player.Position.ChebyshevDistanceTo(monster.Position) > skill.Range)
            {
                return Reject(SkillFailure.OutOfRange, monster);
            }

            if (player.Mana < skill.ManaCost)
            {
                return Reject(SkillFailure.InsufficientMana, monster);
            }

            var key = (characterId, skill.SkillId);
            if (_skillLastCastTicks.TryGetValue(key, out var lastCastTick)
                && currentTick - lastCastTick < skill.CooldownTicks)
            {
                return Reject(SkillFailure.Cooldown, monster);
            }

            player = player with { Mana = player.Mana - skill.ManaCost };
            _players[characterId] = player;
            _skillLastCastTicks[key] = currentTick;
            var damage = Math.Min(player.AttackPower + skill.DamageBonus, monster.Health);
            monster = monster with { Health = monster.Health - damage };
            var defeated = monster.Health == 0;
            WorldDrop? drop = null;
            var experienceGained = 0;
            var levelsGained = 0;
            IReadOnlyList<ProgressionReward>? progressionRewards = null;
            if (defeated)
            {
                _monsters.Remove(monsterId);
                _occupiedPositions.Remove(monster.Position);
                _monsterSpawns[monsterId] = _monsterSpawns[monsterId] with
                {
                    RespawnAtTick = currentTick + MonsterRespawnTicks
                };
                drop = CreateDrop(_monsterSpawns[monsterId].DropItemId,
                    monster.Position, characterId, currentTick);
                progressionRewards = AwardDefeatExperience(player, monster);
                var casterReward = progressionRewards.Single(value => value.CharacterId == characterId);
                player = casterReward.Player;
                experienceGained = casterReward.ExperienceGained;
                levelsGained = casterReward.LevelsGained;
            }
            else
            {
                _monsters[monsterId] = monster;
            }

            return new SkillResult(SkillFailure.None, skillId, player, monster,
                damage, defeated, drop, experienceGained, levelsGained, progressionRewards);
        }
    }

    public ChatResult Chat(Guid characterId, long sequence, string? text, long currentTick)
    {
        lock (_gate)
        {
            if (!_players.TryGetValue(characterId, out var player))
            {
                return new ChatResult(ChatFailure.PlayerNotFound,
                    new WorldPlayer(characterId, default, 0), null);
            }

            if (sequence <= player.LastProcessedSequence)
            {
                return new ChatResult(ChatFailure.StaleSequence, player, null);
            }

            if (!player.IsAlive)
            {
                return new ChatResult(ChatFailure.PlayerDead, player, null);
            }

            player = player with { LastProcessedSequence = sequence };
            _players[characterId] = player;
            var normalized = text?.Trim();
            if (string.IsNullOrWhiteSpace(normalized)
                || normalized.Length > MaximumChatLength
                || normalized.Any(char.IsControl))
            {
                return new ChatResult(ChatFailure.InvalidText, player, null);
            }

            if (_chatLastSentTicks.TryGetValue(characterId, out var lastSentTick)
                && currentTick - lastSentTick < ChatCooldownTicks)
            {
                return new ChatResult(ChatFailure.Cooldown, player, null);
            }

            _chatLastSentTicks[characterId] = currentTick;
            return new ChatResult(ChatFailure.None, player, normalized);
        }
    }

    public ChatResult PartyChat(Guid characterId, long sequence, string? text, long currentTick)
    {
        lock (_gate)
        {
            if (!_players.TryGetValue(characterId, out var player))
            {
                return new ChatResult(ChatFailure.PlayerNotFound,
                    new WorldPlayer(characterId, default, 0), null);
            }

            if (sequence <= player.LastProcessedSequence)
            {
                return new ChatResult(ChatFailure.StaleSequence, player, null);
            }

            if (!player.IsAlive)
            {
                return new ChatResult(ChatFailure.PlayerDead, player, null);
            }

            player = player with { LastProcessedSequence = sequence };
            _players[characterId] = player;
            if (!_partyByCharacter.TryGetValue(characterId, out var partyId))
            {
                return new ChatResult(ChatFailure.NotInParty, player, null);
            }

            var normalized = text?.Trim();
            if (string.IsNullOrWhiteSpace(normalized)
                || normalized.Length > MaximumChatLength
                || normalized.Any(char.IsControl))
            {
                return new ChatResult(ChatFailure.InvalidText, player, null);
            }

            if (_chatLastSentTicks.TryGetValue(characterId, out var lastSentTick)
                && currentTick - lastSentTick < ChatCooldownTicks)
            {
                return new ChatResult(ChatFailure.Cooldown, player, null);
            }

            _chatLastSentTicks[characterId] = currentTick;
            return new ChatResult(
                ChatFailure.None,
                player,
                normalized,
                _parties[partyId].MemberCharacterIds.ToArray());
        }
    }

    public UseItemResult UseItem(Guid characterId, long sequence, string itemId)
    {
        lock (_gate)
        {
            if (!_players.TryGetValue(characterId, out var player))
            {
                return new UseItemResult(UseItemFailure.PlayerNotFound,
                    new WorldPlayer(characterId, default, 0), []);
            }

            UseItemResult Reject(UseItemFailure failure) =>
                new(failure, player, InventorySnapshot(characterId));

            if (sequence <= player.LastProcessedSequence)
            {
                return Reject(UseItemFailure.StaleSequence);
            }

            if (!player.IsAlive)
            {
                return Reject(UseItemFailure.PlayerDead);
            }

            player = player with { LastProcessedSequence = sequence };
            _players[characterId] = player;
            if (!ConsumableCatalog.TryGet(itemId, out var definition))
            {
                return Reject(UseItemFailure.ItemNotConsumable);
            }

            var inventory = _inventories[characterId];
            if (!inventory.TryGetValue(itemId, out var quantity) || quantity <= 0)
            {
                return Reject(UseItemFailure.ItemNotFound);
            }

            var health = Math.Min(player.MaximumHealth, player.Health + definition.HealthRestore);
            var mana = Math.Min(player.MaximumMana, player.Mana + definition.ManaRestore);
            if (health == player.Health && mana == player.Mana)
            {
                return Reject(UseItemFailure.NoEffect);
            }

            if (quantity == 1)
            {
                inventory.Remove(itemId);
            }
            else
            {
                inventory[itemId] = quantity - 1;
            }

            player = player with { Health = health, Mana = mana };
            _players[characterId] = player;
            return new UseItemResult(UseItemFailure.None, player, InventorySnapshot(characterId));
        }
    }

    public PickupResult Pickup(
        Guid characterId,
        long sequence,
        Guid dropId,
        long currentTick)
    {
        lock (_gate)
        {
            if (!_players.TryGetValue(characterId, out var player))
            {
                return new PickupResult(
                    PickupFailure.PlayerNotFound,
                    new WorldPlayer(characterId, default, 0),
                    null,
                    null);
            }

            if (sequence <= player.LastProcessedSequence)
            {
                return new PickupResult(PickupFailure.StaleSequence, player, null, null);
            }

            if (!player.IsAlive)
            {
                return new PickupResult(PickupFailure.PlayerDead, player, null, null);
            }

            player = player with { LastProcessedSequence = sequence };
            _players[characterId] = player;
            if (!_groundDrops.TryGetValue(dropId, out var drop))
            {
                return new PickupResult(PickupFailure.DropNotFound, player, null, null);
            }

            if (currentTick >= drop.ExpiresAtTick)
            {
                _groundDrops.Remove(dropId);
                return new PickupResult(PickupFailure.DropNotFound, player, null, null);
            }

            if (player.Position.ChebyshevDistanceTo(drop.Position) > 1)
            {
                return new PickupResult(PickupFailure.OutOfRange, player, drop, null);
            }

            if (drop.OwnerCharacterId != characterId && currentTick < drop.OwnerProtectionUntilTick)
            {
                return new PickupResult(PickupFailure.Protected, player, drop, null);
            }

            var inventory = _inventories[characterId];
            if (!inventory.ContainsKey(drop.ItemId) && inventory.Count >= InventorySlotLimit)
            {
                return new PickupResult(PickupFailure.InventoryFull, player, drop, null);
            }

            inventory.TryGetValue(drop.ItemId, out var currentQuantity);
            var quantity = checked(currentQuantity + drop.Quantity);
            inventory[drop.ItemId] = quantity;
            _groundDrops.Remove(dropId);
            return new PickupResult(
                PickupFailure.None,
                player,
                drop,
                new InventoryItem(drop.ItemId, quantity));
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
            _playerSpawns.Remove(characterId);
            _chatLastSentTicks.Remove(characterId);
            foreach (var key in _skillLastCastTicks.Keys.Where(key => key.CharacterId == characterId).ToArray())
            {
                _skillLastCastTicks.Remove(key);
            }
            RemoveFromParty(characterId);
            _partyInvitations.Remove(characterId);
            foreach (var targetId in _partyInvitations
                         .Where(value => value.Value.InviterCharacterId == characterId)
                         .Select(value => value.Key)
                         .ToArray())
            {
                _partyInvitations.Remove(targetId);
            }
            return true;
        }
    }

    public PartyInviteResult InviteToParty(
        Guid characterId,
        long sequence,
        Guid targetCharacterId,
        long currentTick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(currentTick);
        lock (_gate)
        {
            if (!TryBeginPartyCommand(characterId, sequence, out var player, out var failure))
            {
                return new PartyInviteResult(failure, player, targetCharacterId, 0);
            }

            PartyInviteResult Reject(PartyFailure reason) =>
                new(reason, player, targetCharacterId, 0);

            if (targetCharacterId == characterId)
            {
                return Reject(PartyFailure.InvalidTarget);
            }

            if (!_players.ContainsKey(targetCharacterId))
            {
                return Reject(PartyFailure.TargetNotFound);
            }

            if (_partyByCharacter.ContainsKey(targetCharacterId))
            {
                return Reject(PartyFailure.TargetAlreadyInParty);
            }

            if (_partyByCharacter.TryGetValue(characterId, out var partyId))
            {
                var party = _parties[partyId];
                if (party.LeaderCharacterId != characterId)
                {
                    return Reject(PartyFailure.NotLeader);
                }

                if (party.MemberCharacterIds.Count >= MaximumPartyMembers)
                {
                    return Reject(PartyFailure.PartyFull);
                }
            }

            if (_partyInvitations.TryGetValue(targetCharacterId, out var pending)
                && pending.ExpiresAtTick > currentTick
                && pending.InviterCharacterId != characterId)
            {
                return Reject(PartyFailure.InvitePending);
            }

            var expiresAtTick = currentTick + PartyInviteLifetimeTicks;
            _partyInvitations[targetCharacterId] = new PartyInvitation(
                characterId,
                targetCharacterId,
                expiresAtTick);
            return new PartyInviteResult(
                PartyFailure.None,
                player,
                targetCharacterId,
                expiresAtTick);
        }
    }

    public PartyChangeResult AcceptPartyInvite(
        Guid characterId,
        long sequence,
        Guid inviterCharacterId,
        long currentTick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(currentTick);
        lock (_gate)
        {
            if (!TryBeginPartyCommand(characterId, sequence, out var player, out var failure))
            {
                return new PartyChangeResult(failure, player, null, []);
            }

            PartyChangeResult Reject(PartyFailure reason) => new(reason, player, null, []);
            if (_partyByCharacter.ContainsKey(characterId))
            {
                return Reject(PartyFailure.AlreadyInParty);
            }

            if (!_partyInvitations.TryGetValue(characterId, out var invitation)
                || invitation.InviterCharacterId != inviterCharacterId)
            {
                return Reject(PartyFailure.InviteNotFound);
            }

            if (invitation.ExpiresAtTick <= currentTick)
            {
                _partyInvitations.Remove(characterId);
                return Reject(PartyFailure.InviteExpired);
            }

            if (!_players.ContainsKey(inviterCharacterId))
            {
                _partyInvitations.Remove(characterId);
                return Reject(PartyFailure.TargetNotFound);
            }

            WorldPartyState party;
            if (_partyByCharacter.TryGetValue(inviterCharacterId, out var partyId))
            {
                party = _parties[partyId];
                if (party.LeaderCharacterId != inviterCharacterId)
                {
                    return Reject(PartyFailure.NotLeader);
                }

                if (party.MemberCharacterIds.Count >= MaximumPartyMembers)
                {
                    return Reject(PartyFailure.PartyFull);
                }
            }
            else
            {
                party = new WorldPartyState(Guid.NewGuid(), inviterCharacterId, [inviterCharacterId]);
                _parties.Add(party.PartyId, party);
                _partyByCharacter.Add(inviterCharacterId, party.PartyId);
            }

            party.MemberCharacterIds.Add(characterId);
            _partyByCharacter.Add(characterId, party.PartyId);
            _partyInvitations.Remove(characterId);
            var snapshot = ToPartySnapshot(party);
            return new PartyChangeResult(
                PartyFailure.None,
                player,
                snapshot,
                snapshot.MemberCharacterIds);
        }
    }

    public PartyChangeResult LeaveParty(Guid characterId, long sequence)
    {
        lock (_gate)
        {
            if (!TryBeginPartyCommand(characterId, sequence, out var player, out var failure))
            {
                return new PartyChangeResult(failure, player, null, []);
            }

            if (!_partyByCharacter.TryGetValue(characterId, out var partyId))
            {
                return new PartyChangeResult(PartyFailure.NotInParty, player, null, []);
            }

            var affected = _parties[partyId].MemberCharacterIds.ToArray();
            var remaining = RemoveFromParty(characterId);
            return new PartyChangeResult(PartyFailure.None, player, remaining, affected);
        }
    }

    public PartySnapshot? PartyFor(Guid characterId)
    {
        lock (_gate)
        {
            return _partyByCharacter.TryGetValue(characterId, out var partyId)
                ? ToPartySnapshot(_parties[partyId])
                : null;
        }
    }

    public WorldPlayer? FindPlayer(Guid characterId)
    {
        lock (_gate)
        {
            return _players.GetValueOrDefault(characterId);
        }
    }

    public WorldPlayer? RestoreResources(
        Guid characterId,
        int? health,
        int? mana,
        long currentTick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(currentTick);
        lock (_gate)
        {
            if (!_players.TryGetValue(characterId, out var player))
            {
                return null;
            }

            var restoredHealth = Math.Clamp(health ?? player.Health, 0, player.MaximumHealth);
            player = player with
            {
                Health = restoredHealth,
                Mana = Math.Clamp(mana ?? player.Mana, 0, player.MaximumMana),
                RespawnAtTick = restoredHealth == 0 ? currentTick + PlayerRespawnTicks : null
            };
            _players[characterId] = player;
            if (restoredHealth == 0)
            {
                _occupiedPositions.Remove(player.Position);
            }

            return player;
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

    public IReadOnlyList<WorldDrop> DropSnapshot()
    {
        lock (_gate)
        {
            return _groundDrops.Values.OrderBy(drop => drop.DropId).ToArray();
        }
    }

    public IReadOnlyList<WorldDrop> DropsVisibleTo(Guid characterId, int radius)
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

            return _groundDrops.Values
                .Where(drop => observer.Position.ChebyshevDistanceTo(drop.Position) <= radius)
                .OrderBy(drop => drop.DropId)
                .ToArray();
        }
    }

    public IReadOnlyList<InventoryItem> InventorySnapshot(Guid characterId)
    {
        lock (_gate)
        {
            return _inventories.TryGetValue(characterId, out var inventory)
                ? inventory.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => new InventoryItem(item.Key, item.Value))
                    .ToArray()
                : [];
        }
    }

    public IReadOnlyList<EquippedItem> EquipmentSnapshot(Guid characterId)
    {
        lock (_gate)
        {
            return _equipment.TryGetValue(characterId, out var equipment)
                ? equipment.OrderBy(item => item.Key)
                    .Select(item => new EquippedItem(item.Key, item.Value))
                    .ToArray()
                : [];
        }
    }

    public EquipmentResult Equip(Guid characterId, long sequence, string itemId)
    {
        lock (_gate)
        {
            if (!TryBeginEquipmentCommand(characterId, sequence, out var player, out var failure))
            {
                return EquipmentFailureResult(failure, player);
            }

            if (!EquipmentCatalog.TryGet(itemId, out var definition))
            {
                return EquipmentFailureResult(EquipmentFailure.ItemNotEquippable, player);
            }

            var inventory = _inventories[characterId];
            if (!inventory.TryGetValue(itemId, out var quantity) || quantity <= 0)
            {
                return EquipmentFailureResult(EquipmentFailure.ItemNotFound, player);
            }

            if (quantity == 1)
            {
                inventory.Remove(itemId);
            }
            else
            {
                inventory[itemId] = quantity - 1;
            }

            var equipment = _equipment[characterId];
            if (equipment.TryGetValue(definition.Slot, out var replacedItemId))
            {
                inventory.TryGetValue(replacedItemId, out var replacedQuantity);
                inventory[replacedItemId] = checked(replacedQuantity + 1);
            }

            equipment[definition.Slot] = itemId;
            player = ApplyEquipmentStats(player);
            _players[characterId] = player;
            return new EquipmentResult(
                EquipmentFailure.None,
                player,
                InventorySnapshot(characterId),
                EquipmentSnapshot(characterId));
        }
    }

    public EquipmentResult Unequip(Guid characterId, long sequence, EquipmentSlot slot)
    {
        lock (_gate)
        {
            if (!TryBeginEquipmentCommand(characterId, sequence, out var player, out var failure))
            {
                return EquipmentFailureResult(failure, player);
            }

            var equipment = _equipment[characterId];
            if (!equipment.TryGetValue(slot, out var itemId))
            {
                return EquipmentFailureResult(EquipmentFailure.SlotEmpty, player);
            }

            var inventory = _inventories[characterId];
            if (!inventory.ContainsKey(itemId) && inventory.Count >= InventorySlotLimit)
            {
                return EquipmentFailureResult(EquipmentFailure.InventoryFull, player);
            }

            inventory.TryGetValue(itemId, out var quantity);
            inventory[itemId] = checked(quantity + 1);
            equipment.Remove(slot);
            player = ApplyEquipmentStats(player);
            _players[characterId] = player;
            return new EquipmentResult(
                EquipmentFailure.None,
                player,
                InventorySnapshot(characterId),
                EquipmentSnapshot(characterId));
        }
    }

    public void ReplaceInventory(Guid characterId, IReadOnlyList<InventoryItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > InventorySlotLimit
            || items.Any(item => string.IsNullOrWhiteSpace(item.ItemId) || item.Quantity <= 0)
            || items.Select(item => item.ItemId).Distinct(StringComparer.Ordinal).Count() != items.Count)
        {
            throw new ArgumentException("Inventory snapshot is invalid.", nameof(items));
        }

        lock (_gate)
        {
            _inventories[characterId] = items.ToDictionary(
                item => item.ItemId,
                item => item.Quantity,
                StringComparer.Ordinal);
        }
    }

    public void ReplaceEquipment(Guid characterId, IReadOnlyList<EquippedItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > Enum.GetValues<EquipmentSlot>().Length
            || items.Select(item => item.Slot).Distinct().Count() != items.Count
            || items.Any(item => !EquipmentCatalog.TryGet(item.ItemId, out var definition)
                || definition.Slot != item.Slot))
        {
            throw new ArgumentException("Equipment snapshot is invalid.", nameof(items));
        }

        lock (_gate)
        {
            _equipment[characterId] = items.ToDictionary(item => item.Slot, item => item.ItemId);
            if (_players.TryGetValue(characterId, out var player))
            {
                _players[characterId] = ApplyEquipmentStats(player);
            }
        }
    }

    public int Advance(long currentTick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(currentTick);
        lock (_gate)
        {
            var changes = 0;
            foreach (var targetId in _partyInvitations
                         .Where(value => value.Value.ExpiresAtTick <= currentTick)
                         .Select(value => value.Key)
                         .ToArray())
            {
                _partyInvitations.Remove(targetId);
                changes++;
            }
            foreach (var expiredDrop in _groundDrops.Values
                         .Where(drop => drop.ExpiresAtTick <= currentTick)
                         .Select(drop => drop.DropId)
                         .ToArray())
            {
                _groundDrops.Remove(expiredDrop);
                changes++;
            }

            foreach (var player in _players.Values
                         .Where(value => value.RespawnAtTick <= currentTick)
                         .OrderBy(value => value.CharacterId)
                         .ToArray())
            {
                var spawn = _playerSpawns[player.CharacterId];
                if (_occupiedPositions.Contains(spawn))
                {
                    continue;
                }

                _players[player.CharacterId] = player with
                {
                    Position = spawn,
                    Health = player.MaximumHealth,
                    Mana = player.MaximumMana,
                    RespawnAtTick = null,
                    InvulnerableUntilTick = currentTick + PlayerRespawnProtectionTicks
                };
                _occupiedPositions.Add(spawn);
                changes++;
            }

            if (currentTick % ManaRegenerationIntervalTicks == 0)
            {
                foreach (var player in _players.Values.Where(value => value.IsAlive && value.Mana < value.MaximumMana).ToArray())
                {
                    _players[player.CharacterId] = player with { Mana = player.Mana + 1 };
                    changes++;
                }
            }

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
                    .Where(player => player.IsAlive && player.InvulnerableUntilTick <= currentTick)
                    .Select(player => new
                    {
                        Player = player,
                        Distance = monster.Position.ChebyshevDistanceTo(player.Position)
                    })
                    .Where(value => value.Distance <= MonsterAggroRadius)
                    .OrderBy(value => value.Distance)
                    .ThenBy(value => value.Player.CharacterId)
                    .FirstOrDefault();
                if (target is null)
                {
                    continue;
                }


                if (target.Distance <= 1)
                {
                    if (currentTick - monster.LastAttackTick < MonsterAttackCooldownTicks)
                    {
                        continue;
                    }

                    var attackingMonster = monster with { LastAttackTick = currentTick };
                    _monsters[monster.MonsterId] = attackingMonster;
                    var receivedDamage = Math.Max(1, MonsterAttackDamage - target.Player.Defense);
                    var health = Math.Max(0, target.Player.Health - receivedDamage);
                    var damagedPlayer = target.Player with
                    {
                        Health = health,
                        RespawnAtTick = health == 0 ? currentTick + PlayerRespawnTicks : null
                    };
                    _players[target.Player.CharacterId] = damagedPlayer;
                    if (health == 0)
                    {
                        _occupiedPositions.Remove(target.Player.Position);
                    }

                    changes++;
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

    private static WorldPlayer CreatePlayer(
        Guid characterId,
        GridPosition position,
        CharacterArchetype archetype,
        int level = CharacterProgression.MinimumLevel,
        long experience = 0)
    {
        if (level is < CharacterProgression.MinimumLevel or > CharacterProgression.MaximumLevel
            || CharacterProgression.LevelForExperience(experience) != level)
        {
            throw new ArgumentOutOfRangeException(nameof(level), "Level must match cumulative experience.");
        }

        var levelOffset = level - 1;
        return archetype switch
        {
            CharacterArchetype.Warrior => new WorldPlayer(
                characterId, position, 0, MaximumHealth: 120 + levelOffset * 12,
                Health: 120 + levelOffset * 12, Archetype: archetype,
                AttackPower: 12 + levelOffset * 2, Defense: 3 + levelOffset / 3,
                AttackCooldownTicks: 10, Mana: 30 + levelOffset * 2,
                MaximumMana: 30 + levelOffset * 2, Level: level, Experience: experience),
            CharacterArchetype.Mage => new WorldPlayer(
                characterId, position, 0, MaximumHealth: 80 + levelOffset * 7,
                Health: 80 + levelOffset * 7, Archetype: archetype,
                AttackPower: 14 + levelOffset * 3, Defense: 1 + levelOffset / 4,
                AttackCooldownTicks: 12, Mana: 60 + levelOffset * 8,
                MaximumMana: 60 + levelOffset * 8, Level: level, Experience: experience),
            CharacterArchetype.Taoist => new WorldPlayer(
                characterId, position, 0, MaximumHealth: 95 + levelOffset * 9,
                Health: 95 + levelOffset * 9, Archetype: archetype,
                AttackPower: 10 + levelOffset * 2, Defense: 2 + levelOffset / 3,
                AttackCooldownTicks: 11, Mana: 50 + levelOffset * 6,
                MaximumMana: 50 + levelOffset * 6, Level: level, Experience: experience),
            CharacterArchetype.Assassin => new WorldPlayer(
                characterId, position, 0, MaximumHealth: 90 + levelOffset * 8,
                Health: 90 + levelOffset * 8, Archetype: archetype,
                AttackPower: 13 + levelOffset * 3, Defense: 1 + levelOffset / 4,
                AttackCooldownTicks: 8, Mana: 35 + levelOffset * 4,
                MaximumMana: 35 + levelOffset * 4, Level: level, Experience: experience),
            _ => throw new ArgumentOutOfRangeException(nameof(archetype))
        };
    }

    private bool TryBeginEquipmentCommand(
        Guid characterId,
        long sequence,
        out WorldPlayer player,
        out EquipmentFailure failure)
    {
        if (!_players.TryGetValue(characterId, out player!))
        {
            player = new WorldPlayer(characterId, default, 0);
            failure = EquipmentFailure.PlayerNotFound;
            return false;
        }

        if (sequence <= player.LastProcessedSequence)
        {
            failure = EquipmentFailure.StaleSequence;
            return false;
        }

        if (!player.IsAlive)
        {
            failure = EquipmentFailure.PlayerDead;
            return false;
        }

        player = player with { LastProcessedSequence = sequence };
        _players[characterId] = player;
        failure = EquipmentFailure.None;
        return true;
    }

    private bool TryBeginPartyCommand(
        Guid characterId,
        long sequence,
        out WorldPlayer player,
        out PartyFailure failure)
    {
        if (!_players.TryGetValue(characterId, out player!))
        {
            player = new WorldPlayer(characterId, default, 0);
            failure = PartyFailure.PlayerNotFound;
            return false;
        }

        if (sequence <= player.LastProcessedSequence)
        {
            failure = PartyFailure.StaleSequence;
            return false;
        }

        player = player with { LastProcessedSequence = sequence };
        _players[characterId] = player;
        failure = PartyFailure.None;
        return true;
    }

    private PartySnapshot? RemoveFromParty(Guid characterId)
    {
        if (!_partyByCharacter.Remove(characterId, out var partyId))
        {
            return null;
        }

        var party = _parties[partyId];
        party.MemberCharacterIds.Remove(characterId);
        if (party.MemberCharacterIds.Count < 2)
        {
            _parties.Remove(partyId);
            foreach (var memberId in party.MemberCharacterIds)
            {
                _partyByCharacter.Remove(memberId);
            }
            return null;
        }

        if (party.LeaderCharacterId == characterId)
        {
            party.LeaderCharacterId = party.MemberCharacterIds[0];
        }

        return ToPartySnapshot(party);
    }

    private static PartySnapshot ToPartySnapshot(WorldPartyState party) =>
        new(party.PartyId, party.LeaderCharacterId, party.MemberCharacterIds.ToArray());

    private EquipmentResult EquipmentFailureResult(EquipmentFailure failure, WorldPlayer player) =>
        new(
            failure,
            player,
            InventorySnapshot(player.CharacterId),
            EquipmentSnapshot(player.CharacterId));

    private WorldPlayer ApplyEquipmentStats(WorldPlayer player)
    {
        var basePlayer = CreatePlayer(
            player.CharacterId,
            player.Position,
            player.Archetype,
            player.Level,
            player.Experience);
        var definitions = EquipmentSnapshot(player.CharacterId)
            .Select(item => EquipmentCatalog.TryGet(item.ItemId, out var definition) ? definition : null)
            .Where(definition => definition is not null)
            .Cast<EquipmentDefinition>()
            .ToArray();
        var maximumHealth = basePlayer.MaximumHealth + definitions.Sum(item => item.MaximumHealthBonus);
        var health = maximumHealth > player.MaximumHealth
            ? Math.Min(maximumHealth, player.Health + maximumHealth - player.MaximumHealth)
            : Math.Min(maximumHealth, player.Health);
        return player with
        {
            Health = health,
            MaximumHealth = maximumHealth,
            AttackPower = basePlayer.AttackPower + definitions.Sum(item => item.AttackBonus),
            Defense = basePlayer.Defense + definitions.Sum(item => item.DefenseBonus)
        };
    }

    private IReadOnlyList<ProgressionReward> AwardDefeatExperience(
        WorldPlayer player,
        WorldMonster monster)
    {
        var eligible = _partyByCharacter.TryGetValue(player.CharacterId, out var partyId)
            ? _parties[partyId].MemberCharacterIds
                .Select(characterId => _players.GetValueOrDefault(characterId))
                .Where(member => member is not null
                    && member.IsAlive
                    && member.Position.ChebyshevDistanceTo(monster.Position) <= PartyExperienceRadius)
                .Cast<WorldPlayer>()
                .ToArray()
            : [player];
        if (!eligible.Any(member => member.CharacterId == player.CharacterId))
        {
            eligible = [.. eligible, player];
        }

        var groupMultiplier = 100 + (eligible.Length - 1) * PartyExperienceBonusPercentPerAdditionalMember;
        var rewards = new List<ProgressionReward>(eligible.Length);
        foreach (var member in eligible)
        {
            var soloReward = CharacterProgression.ExperienceReward(member.Level, monster.Level);
            var sharedReward = (int)Math.Max(
                1,
                (long)soloReward * groupMultiplier / (100L * eligible.Length));
            var (updated, levelsGained) = ApplyExperience(member, sharedReward);
            rewards.Add(new ProgressionReward(
                member.CharacterId,
                sharedReward,
                levelsGained,
                updated));
        }

        return rewards;
    }

    private (WorldPlayer Player, int LevelsGained) ApplyExperience(
        WorldPlayer player,
        int reward)
    {
        var experience = player.Experience > long.MaxValue - reward
            ? long.MaxValue
            : player.Experience + reward;
        var level = CharacterProgression.LevelForExperience(experience);
        var levelsGained = level - player.Level;
        if (levelsGained == 0)
        {
            player = player with { Experience = experience };
            _players[player.CharacterId] = player;
            return (player, 0);
        }

        var previousMaximumMana = player.MaximumMana;
        player = player with { Level = level, Experience = experience };
        var leveledBase = CreatePlayer(
            player.CharacterId,
            player.Position,
            player.Archetype,
            level,
            experience);
        player = player with
        {
            Mana = Math.Min(
                leveledBase.MaximumMana,
                player.Mana + leveledBase.MaximumMana - previousMaximumMana),
            MaximumMana = leveledBase.MaximumMana
        };
        player = ApplyEquipmentStats(player);
        _players[player.CharacterId] = player;
        return (player, levelsGained);
    }

    private WorldDrop CreateDrop(
        string itemId,
        GridPosition position,
        Guid ownerCharacterId,
        long currentTick)
    {
        Span<byte> bytes = stackalloc byte[16];
        ownerCharacterId.TryWriteBytes(bytes);
        BitConverter.TryWriteBytes(bytes[8..], ++_nextDropSequence);
        var drop = new WorldDrop(
            new Guid(bytes),
            itemId,
            1,
            position,
            ownerCharacterId,
            currentTick + DropOwnerProtectionTicks,
            currentTick + DropLifetimeTicks);
        _groundDrops.Add(drop.DropId, drop);
        return drop;
    }

    private sealed record MonsterSpawn(
        Guid MonsterId,
        string TemplateId,
        GridPosition Position,
        int Level,
        int MaximumHealth,
        string DropItemId,
        long? RespawnAtTick);

    private sealed class WorldPartyState(
        Guid partyId,
        Guid leaderCharacterId,
        List<Guid> memberCharacterIds)
    {
        public Guid PartyId { get; } = partyId;
        public Guid LeaderCharacterId { get; set; } = leaderCharacterId;
        public List<Guid> MemberCharacterIds { get; } = memberCharacterIds;
    }

    private sealed record PartyInvitation(
        Guid InviterCharacterId,
        Guid TargetCharacterId,
        long ExpiresAtTick);
}
