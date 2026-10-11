namespace Project.Services;

public sealed class HoldPickupExpiryScheduledWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<HoldPickupExpiryScheduledWorker> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
            var due = HoldPickupExpirySchedule.IsDue(nowUtc);
            var outcome = HoldPickupScheduledRunOutcome.NotDue;
            if (due)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var scheduler = scope.ServiceProvider.GetRequiredService<IHoldPickupExpirySchedulingService>();
                    outcome = await scheduler.RunScheduledIfDueAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Scheduled hold expiry could not check or update its daily run record.");
                    outcome = HoldPickupScheduledRunOutcome.Failed;
                }
            }

            var retry = due && outcome is (HoldPickupScheduledRunOutcome.Failed or HoldPickupScheduledRunOutcome.Skipped or HoldPickupScheduledRunOutcome.InProgress);
            var waitFromUtc = timeProvider.GetUtcNow().UtcDateTime;
            var delay = retry
                ? RetryDelay
                : HoldPickupExpirySchedule.GetNextRunUtc(waitFromUtc) - waitFromUtc;
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

            try
            {
                await Task.Delay(delay, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
