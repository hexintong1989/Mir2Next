using Mirage.Shared.Contracts;

namespace Mirage.Domain.World;

public sealed record SkillDefinition(
    string SkillId,
    CharacterArchetype Archetype,
    int Range,
    int ManaCost,
    int CooldownTicks,
    int DamageBonus);

public static class SkillCatalog
{
    private static readonly Dictionary<string, SkillDefinition> Definitions = new(StringComparer.Ordinal)
    {
        ["power_strike"] = new("power_strike", CharacterArchetype.Warrior, 1, 8, 20, 8),
        ["fireball"] = new("fireball", CharacterArchetype.Mage, 6, 12, 24, 10),
        ["soul_talisman"] = new("soul_talisman", CharacterArchetype.Taoist, 5, 10, 22, 7),
        ["shadow_strike"] = new("shadow_strike", CharacterArchetype.Assassin, 2, 9, 18, 9)
    };

    public static bool TryGet(string? skillId, out SkillDefinition definition) =>
        Definitions.TryGetValue(skillId ?? string.Empty, out definition!);

    public static IReadOnlyList<SkillDefinition> ForArchetype(CharacterArchetype archetype) =>
        Definitions.Values.Where(skill => skill.Archetype == archetype)
            .OrderBy(skill => skill.SkillId, StringComparer.Ordinal).ToArray();
}
