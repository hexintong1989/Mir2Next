namespace Mirage.WorldServer;

public sealed class WorldBroadcastService(
    WorldSocketHub socketHub,
    ILogger<WorldBroadcastService> logger) : BackgroundService
{
    private static readonly TimeSpan BroadcastInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(BroadcastInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await socketHub.BroadcastMonsterSnapshotsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "World snapshot broadcast failed; retrying next interval.");
            }
        }
    }
}
