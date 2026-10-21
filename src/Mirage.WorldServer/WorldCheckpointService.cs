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
            foreach (var player in world.Snapshot())
            {
                try
                {
                    await worldStates.SaveWorldStateAsync(new CharacterWorldState(
                        player.CharacterId,
                        world.MapId,
                        player.Position.X,
                        player.Position.Y,
                        savedAt,
                        player.Health,
                        player.Mana), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception,
                        "Failed to checkpoint world state for {CharacterId}.", player.CharacterId);
                }
            }
        }
    }
}
