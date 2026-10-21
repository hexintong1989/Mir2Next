using Mirage.Shared.Contracts;

namespace Mirage.Domain.World;

public sealed record SkillDefinition(
    string SkillId,
    CharacterArchetype Archetype,
    int Range,
    int ManaCost,
    int CooldownTicks,
    int DamageBonus,
    int MinimumLevel);

public static class SkillCatalog
{
    private static readonly Dictionary<string, SkillDefinition> Definitions = new(StringComparer.Ordinal)
    {
        ["power_strike"] = new("power_strike", CharacterArchetype.Warrior, 1, 8, 20, 8, 1),
        ["whirlwind"] = new("whirlwind", CharacterArchetype.Warrior, 1, 18, 40, 18, 5),
        ["fireball"] = new("fireball", CharacterArchetype.Mage, 6, 12, 24, 10, 1),
        ["lightning_bolt"] = new("lightning_bolt", CharacterArchetype.Mage, 7, 24, 36, 24, 5),
        ["soul_talisman"] = new("soul_talisman", CharacterArchetype.Taoist, 5, 10, 22, 7, 1),
        ["poison_talisman"] = new("poison_talisman", CharacterArchetype.Taoist, 5, 20, 34, 20, 5),
        ["shadow_strike"] = new("shadow_strike", CharacterArchetype.Assassin, 2, 9, 18, 9, 1),
        ["blade_flurry"] = new("blade_flurry", CharacterArchetype.Assassin, 2, 19, 30, 21, 5)
    };

    public static bool TryGet(string? skillId, out SkillDefinition definition) =>
        Definitions.TryGetValue(skillId ?? string.Empty, out definition!);

    public static IReadOnlyList<SkillDefinition> ForArchetype(CharacterArchetype archetype) =>
        Definitions.Values.Where(skill => skill.Archetype == archetype)
            .OrderBy(skill => skill.SkillId, StringComparer.Ordinal).ToArray();
}
