using Mirage.Domain.World;

namespace Mirage.WorldServer;

public sealed class WorldTickService(
    WorldSimulation simulation,
    ILogger<WorldTickService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "World simulation loop started at {TicksPerSecond} ticks per second.",
            1000 / TickInterval.TotalMilliseconds);

        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            simulation.Tick();
        }
    }
}
