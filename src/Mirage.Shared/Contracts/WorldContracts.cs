namespace Mirage.Shared.Contracts;

public sealed record MapResponse(string MapId, int Width, int Height, int PlayerCount, int MonsterCount);

public sealed record JoinWorldRequest(Guid CharacterId, int X, int Y);

public sealed record MoveRequest(long Sequence, int X, int Y);

public sealed record AttackRequest(long Sequence, Guid MonsterId);

public sealed record CastSkillRequest(long Sequence, string? SkillId, Guid MonsterId);

public sealed record ChatRequest(long Sequence, string? Text);

public sealed record UseItemRequest(long Sequence, string? ItemId);

public sealed record PickupRequest(long Sequence, Guid DropId);

public sealed record EquipItemRequest(long Sequence, string? ItemId);

public sealed record UnequipItemRequest(long Sequence, string? Slot);

public sealed record SpawnMonsterRequest(
    Guid MonsterId,
    string TemplateId,
    int X,
    int Y,
    int Level,
    int MaximumHealth);

public sealed record PlayerPositionResponse(
    Guid CharacterId,
    int X,
    int Y,
    long LastProcessedSequence,
    int Health,
    int MaximumHealth,
    bool IsAlive,
    long InvulnerableUntilTick,
    CharacterArchetype Archetype,
    int AttackPower,
    int Defense,
    int AttackCooldownTicks,
    int Mana,
    int MaximumMana);

public sealed record MoveResponse(
    bool Accepted,
    string? Rejection,
    PlayerPositionResponse Player);

public sealed record MonsterResponse(
    Guid MonsterId,
    string TemplateId,
    int X,
    int Y,
    int Level,
    int Health,
    int MaximumHealth);

public sealed record AttackResponse(
    bool Accepted,
    string? Rejection,
    int Damage,
    bool Defeated,
    MonsterResponse? Monster);

public sealed record SkillResponse(
    bool Accepted,
    string? Rejection,
    string SkillId,
    int Damage,
    bool Defeated,
    MonsterResponse? Monster,
    PlayerPositionResponse Player);

public sealed record ChatResponse(bool Accepted, string? Rejection, string? Text);

public sealed record UseItemResponse(
    bool Accepted,
    string? Rejection,
    PlayerPositionResponse Player,
    IReadOnlyList<InventoryItemResponse> Inventory);

public sealed record GroundDropResponse(
    Guid DropId,
    string ItemId,
    int Quantity,
    int X,
    int Y,
    Guid OwnerCharacterId,
    long OwnerProtectionUntilTick,
    long ExpiresAtTick);

public sealed record InventoryItemResponse(string ItemId, int Quantity);

public sealed record EquippedItemResponse(string Slot, string ItemId);

public sealed record PickupResponse(
    bool Accepted,
    string? Rejection,
    GroundDropResponse? Drop,
    InventoryItemResponse? InventoryItem);
