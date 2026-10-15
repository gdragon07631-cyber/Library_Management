using Project.Services;

namespace Project.Services;

public sealed class OverdueReminderWorker(IServiceScopeFactory scopeFactory, TimeProvider clock,
    ILogger<OverdueReminderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var local = HoldPickupExpirySchedule.GetVietnamLocalTime(now);
            var date = DateOnly.FromDateTime(local);
            var targetDate = TimeOnly.FromDateTime(local) < new TimeOnly(7, 0) ? date : date.AddDays(1);
            var delay = HoldPickupExpirySchedule.ToUtc(targetDate, new TimeOnly(7, 0)) - now;
            try { await Task.Delay(delay, clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IOverdueReminderService>().RunAsync(null, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Scheduled overdue email reminders failed"); }
        }
    }
}
