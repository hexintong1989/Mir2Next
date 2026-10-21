namespace Mirage.Domain.World;

public enum EquipmentSlot
{
    Weapon = 1,
    Armor = 2,
    Accessory = 3
}

public sealed record EquippedItem(EquipmentSlot Slot, string ItemId);

public sealed record EquipmentDefinition(
    string ItemId,
    EquipmentSlot Slot,
    int AttackBonus,
    int DefenseBonus,
    int MaximumHealthBonus);

public static class EquipmentCatalog
{
    private static readonly IReadOnlyDictionary<string, EquipmentDefinition> Definitions =
        new Dictionary<string, EquipmentDefinition>(StringComparer.Ordinal)
        {
            ["wooden_sword"] = new("wooden_sword", EquipmentSlot.Weapon, 3, 0, 0),
            ["cloth_armor"] = new("cloth_armor", EquipmentSlot.Armor, 0, 2, 10),
            ["wooden_token"] = new("wooden_token", EquipmentSlot.Accessory, 1, 1, 0)
        };

    public static bool TryGet(string itemId, out EquipmentDefinition definition) =>
        Definitions.TryGetValue(itemId, out definition!);
}

public enum EquipmentFailure
{
    None,
    PlayerNotFound,
    PlayerDead,
    StaleSequence,
    ItemNotFound,
    ItemNotEquippable,
    SlotEmpty,
    InventoryFull
}

public readonly record struct EquipmentResult(
    EquipmentFailure Failure,
    WorldPlayer Player,
    IReadOnlyList<InventoryItem> Inventory,
    IReadOnlyList<EquippedItem> Equipment)
{
    public bool IsSuccess => Failure == EquipmentFailure.None;
}
