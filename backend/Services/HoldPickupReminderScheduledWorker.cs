using Microsoft.Extensions.Options;

namespace Project.Services;

public sealed class HoldPickupReminderScheduledWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<HoldPickupReminderOptions> options,
    TimeProvider timeProvider,
    ILogger<HoldPickupReminderScheduledWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!HoldPickupReminderSchedule.TryParseRunTime(options.Value.SendTimeLocal, out var runTime))
            throw new InvalidOperationException("HoldPickupReminder:SendTimeLocal must use HH:mm in Asia/Ho_Chi_Minh.");

        if (options.Value.RetryIntervalSeconds is < 30 or > 3600)
            throw new InvalidOperationException("HoldPickupReminder:RetryIntervalSeconds must be between 30 and 3600.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
            var isDue = HoldPickupReminderSchedule.IsDue(nowUtc, runTime);
            if (isDue)
            {
                var businessDate = HoldPickupExpirySchedule.GetBusinessDate(nowUtc);
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var enqueuer = scope.ServiceProvider.GetRequiredService<HoldPickupReminderEnqueuer>();
                    var queued = await enqueuer.EnqueueForDateAsync(businessDate, stoppingToken);
                    logger.LogInformation("Last-day pickup reminder scan completed for Vietnam date {BusinessDate}; queued {Count} reminders.", businessDate, queued);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Last-day pickup reminder scan failed for Vietnam date {BusinessDate}; it will retry.", businessDate);
                }
            }

            var wait = isDue
                ? TimeSpan.FromSeconds(options.Value.RetryIntervalSeconds)
                : HoldPickupReminderSchedule.GetNextRunUtc(timeProvider.GetUtcNow().UtcDateTime, runTime) - timeProvider.GetUtcNow().UtcDateTime;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            try { await Task.Delay(wait, timeProvider, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
