namespace Mirage.Domain.World;

public static class CharacterProgression
{
    public const int MinimumLevel = 1;
    public const int MaximumLevel = 60;

    public static long ExperienceRequiredForLevel(int level)
    {
        if (level is < MinimumLevel or > MaximumLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        return 100L * (level - 1) * level / 2;
    }

    public static int LevelForExperience(long experience)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(experience);
        var level = MinimumLevel;
        while (level < MaximumLevel
               && experience >= ExperienceRequiredForLevel(level + 1))
        {
            level++;
        }

        return level;
    }

    public static int ExperienceReward(int playerLevel, int monsterLevel)
    {
        if (playerLevel is < MinimumLevel or > MaximumLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(playerLevel));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(monsterLevel, 1);
        var baseReward = 20L + monsterLevel * 15L;
        var levelMultiplier = Math.Clamp(
            100L + ((long)monsterLevel - playerLevel) * 20L,
            25,
            200);
        return (int)Math.Clamp(baseReward * levelMultiplier / 100, 1, int.MaxValue);
    }
}

public sealed record ProgressionReward(
    Guid CharacterId,
    int ExperienceGained,
    int LevelsGained,
    WorldPlayer Player);
