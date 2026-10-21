namespace Mirage.Shared.Contracts;

public sealed record MapResponse(string MapId, int Width, int Height, int PlayerCount, int MonsterCount);

public sealed record JoinWorldRequest(Guid CharacterId, int X, int Y);

public sealed record MoveRequest(long Sequence, int X, int Y);

public sealed record AttackRequest(long Sequence, Guid MonsterId);

public sealed record PickupRequest(long Sequence, Guid DropId);

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
    long LastProcessedSequence);

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

public sealed record GroundDropResponse(
    Guid DropId,
    string ItemId,
    int Quantity,
    int X,
    int Y,
    Guid OwnerCharacterId,
    long OwnerProtectionUntilTick);

public sealed record InventoryItemResponse(string ItemId, int Quantity);

public sealed record PickupResponse(
    bool Accepted,
    string? Rejection,
    GroundDropResponse? Drop,
    InventoryItemResponse? InventoryItem);
