using Mirage.Domain.Accounts;
using Mirage.Domain.Characters;

namespace Mirage.AuthServer;

public sealed class CharacterDeletionCleanupService(
    ICharacterRepository characters,
    IClock clock,
    ILogger<CharacterDeletionCleanupService> logger) : BackgroundService
{
    private const int BatchSize = 100;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deleted = await characters.PurgeDueDeletionsAsync(
                    clock.UtcNow,
                    BatchSize,
                    stoppingToken);
                if (deleted.Count > 0)
                {
                    logger.LogInformation(
                        "Purged {CharacterCount} characters after their deletion cooldown.",
                        deleted.Count);
                }

                if (deleted.Count == BatchSize)
                {
                    continue;
                }

                await Task.Delay(IdleDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Character deletion cleanup failed.");
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }
    }
}
