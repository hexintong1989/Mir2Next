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
        var baseReward = 20 + monsterLevel * 15;
        var levelMultiplier = Math.Clamp(100 + (monsterLevel - playerLevel) * 20, 25, 200);
        return Math.Max(1, baseReward * levelMultiplier / 100);
    }
}
