namespace Project.Services;

/// <summary>
/// Retained for compatibility with prior deployments. This legacy interval worker is not registered;
/// scheduled processing is handled by <see cref="HoldPickupExpiryScheduledWorker"/>.
/// </summary>
public sealed class HoldPickupExpiryWorker : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
