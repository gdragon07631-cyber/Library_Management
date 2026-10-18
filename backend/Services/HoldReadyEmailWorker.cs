using Project.Services;

namespace Project.Services;

public sealed class HoldReadyEmailWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<HoldReadyEmailWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<HoldEmailOutboxProcessor>()
                    .ProcessOneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Hold email worker iteration failed."); }

            try { await Task.Delay(PollInterval, timeProvider, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
