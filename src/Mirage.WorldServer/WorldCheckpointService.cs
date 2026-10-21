using Mirage.Domain.Characters;
using Mirage.Domain.World;

namespace Mirage.WorldServer;

public sealed class WorldCheckpointService(
    WorldInstance world,
    ICharacterWorldStateStore worldStates,
    ILogger<WorldCheckpointService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckpointInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var savedAt = DateTimeOffset.UtcNow;
            var states = world.Snapshot()
                .Select(player => new CharacterWorldState(
                    player.CharacterId,
                    world.MapId,
                    player.Position.X,
                    player.Position.Y,
                    savedAt,
                    player.Health,
                    player.Mana,
                    player.Level,
                    player.Experience))
                .ToArray();
            if (states.Length == 0)
            {
                continue;
            }

            try
            {
                var savedCount = await worldStates.SaveWorldStatesAsync(states, stoppingToken);
                if (savedCount != states.Length)
                {
                    logger.LogWarning(
                        "World checkpoint saved {SavedCount} of {RequestedCount} character states.",
                        savedCount,
                        states.Length);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "Failed to checkpoint {CharacterCount} world states.", states.Length);
            }
        }
    }
}
