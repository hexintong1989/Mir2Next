namespace Mirage.Domain.World;

public sealed record ConsumableDefinition(string ItemId, int HealthRestore, int ManaRestore);

public static class ConsumableCatalog
{
    private static readonly Dictionary<string, ConsumableDefinition> Definitions = new(StringComparer.Ordinal)
    {
        ["venison"] = new("venison", 25, 0),
        ["mana_herb"] = new("mana_herb", 0, 15)
    };

    public static bool TryGet(string? itemId, out ConsumableDefinition definition) =>
        Definitions.TryGetValue(itemId ?? string.Empty, out definition!);
}
