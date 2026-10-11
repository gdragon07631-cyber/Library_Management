using System.Data;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public enum HoldPickupScheduledRunOutcome
{
    NotDue,
    Succeeded,
    Failed,
    AlreadySucceeded,
    InProgress,
    Skipped
}

public static class HoldPickupExpirySchedule
{
    public static readonly TimeOnly DailyRunTime = new(0, 30);
    public const string IanaTimeZoneId = "Asia/Ho_Chi_Minh";

    public static TimeZoneInfo VietnamTimeZone { get; } = ResolveVietnamTimeZone();

    public static DateTime GetVietnamLocalTime(DateTime utcNow) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), VietnamTimeZone);

    public static DateOnly GetBusinessDate(DateTime utcNow) => DateOnly.FromDateTime(GetVietnamLocalTime(utcNow));

    public static bool IsDue(DateTime utcNow) => TimeOnly.FromDateTime(GetVietnamLocalTime(utcNow)) >= DailyRunTime;

    public static DateTime GetNextRunUtc(DateTime utcNow)
    {
        var localNow = GetVietnamLocalTime(utcNow);
        var nextDate = DateOnly.FromDateTime(localNow);
        if (TimeOnly.FromDateTime(localNow) >= DailyRunTime) nextDate = nextDate.AddDays(1);
        var localTarget = DateTime.SpecifyKind(nextDate.ToDateTime(DailyRunTime), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(localTarget, VietnamTimeZone);
    }

    private static TimeZoneInfo ResolveVietnamTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(IanaTimeZoneId); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); }
    }
}

public interface IHoldPickupExpirySchedulingService
{
    Task<HoldPickupScheduledRunOutcome> RunScheduledIfDueAsync(CancellationToken cancellationToken = default);
    Task<HoldPickupExpiryRun?> GetLatestRunAsync(CancellationToken cancellationToken = default);
}

public sealed class HoldPickupExpirySchedulingService(
    ApplicationDbContext db,
    IHoldPickupExpiryService expiryService,
    TimeProvider timeProvider,
    ILogger<HoldPickupExpirySchedulingService> logger) : IHoldPickupExpirySchedulingService
{
    private static readonly TimeSpan AbandonedRunLease = TimeSpan.FromHours(6);

    public Task<HoldPickupExpiryRun?> GetLatestRunAsync(CancellationToken cancellationToken = default) =>
        db.HoldPickupExpiryRuns.AsNoTracking()
            .OrderByDescending(run => run.BusinessDate)
            .ThenByDescending(run => run.StartedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<HoldPickupScheduledRunOutcome> RunScheduledIfDueAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (!HoldPickupExpirySchedule.IsDue(nowUtc)) return HoldPickupScheduledRunOutcome.NotDue;

        var businessDate = HoldPickupExpirySchedule.GetBusinessDate(nowUtc);
        var acquisition = await TryAcquireDailyRunAsync(businessDate, nowUtc, cancellationToken);
        if (acquisition.RunId is null) return acquisition.Outcome;
        var runId = acquisition.RunId.Value;

        try
        {
            var result = await expiryService.ProcessOverdueAsync(nowUtc, cancellationToken);
            var succeeded = result.Errors.Count == 0;
            var message = succeeded
                ? $"Checked {result.CheckedCount}; cancelled {result.CancelledCount}; reassigned {result.AssignedToNextReaderCount}; returned {result.ReturnedToAvailableCount}."
                : string.Join(" ", result.Errors);

            await db.HoldPickupExpiryRuns
                .Where(run => run.Id == runId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(run => run.Status, succeeded ? HoldPickupExpiryRunStatus.Succeeded : HoldPickupExpiryRunStatus.Failed)
                    .SetProperty(run => run.CompletedAtUtc, timeProvider.GetUtcNow().UtcDateTime)
                    .SetProperty(run => run.CheckedCount, run => run.CheckedCount + result.CheckedCount)
                    .SetProperty(run => run.CancelledCount, run => run.CancelledCount + result.CancelledCount)
                    .SetProperty(run => run.AssignedToNextReaderCount, run => run.AssignedToNextReaderCount + result.AssignedToNextReaderCount)
                    .SetProperty(run => run.ReturnedToAvailableCount, run => run.ReturnedToAvailableCount + result.ReturnedToAvailableCount)
                    .SetProperty(run => run.ErrorMessage, succeeded ? null : Truncate(message, 2000))
                    .SetProperty(run => run.ResultMessage, Truncate(message, 500)), cancellationToken);
            db.ChangeTracker.Clear();

            if (succeeded)
                logger.LogInformation("Scheduled overdue hold run succeeded for Vietnam date {BusinessDate}: {Message}", businessDate, message);
            else
                logger.LogError("Scheduled overdue hold run had processing errors for Vietnam date {BusinessDate}: {Message}", businessDate, message);
            return succeeded ? HoldPickupScheduledRunOutcome.Succeeded : HoldPickupScheduledRunOutcome.Failed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await MarkFailedAsync(runId, "The application stopped before the scheduled run completed.", CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Scheduled overdue hold run failed for Vietnam date {BusinessDate}.", businessDate);
            await MarkFailedAsync(runId, "The scheduled run failed due to a system error.", CancellationToken.None);
            return HoldPickupScheduledRunOutcome.Failed;
        }
    }

    private async Task<RunAcquisition> TryAcquireDailyRunAsync(DateOnly businessDate, DateTime nowUtc, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var run = await db.HoldPickupExpiryRuns.SingleOrDefaultAsync(item => item.BusinessDate == businessDate, cancellationToken);
            if (run?.Status == HoldPickupExpiryRunStatus.Succeeded ||
                (run?.Status == HoldPickupExpiryRunStatus.Running && run.StartedAtUtc > nowUtc - AbandonedRunLease))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(null, run?.Status == HoldPickupExpiryRunStatus.Succeeded
                    ? HoldPickupScheduledRunOutcome.AlreadySucceeded
                    : HoldPickupScheduledRunOutcome.InProgress);
            }

            if (run == null)
            {
                run = new HoldPickupExpiryRun { BusinessDate = businessDate };
                db.HoldPickupExpiryRuns.Add(run);
            }
            run.Status = HoldPickupExpiryRunStatus.Running;
            run.StartedAtUtc = nowUtc;
            run.CompletedAtUtc = null;
            run.ErrorMessage = null;
            run.ResultMessage = "Scheduled run is in progress.";
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(run.Id, HoldPickupScheduledRunOutcome.Succeeded);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.HoldPickupExpiryRuns.AsNoTracking().AnyAsync(run => run.BusinessDate == businessDate, cancellationToken))
            {
                var existing = await db.HoldPickupExpiryRuns.AsNoTracking()
                    .SingleAsync(run => run.BusinessDate == businessDate, cancellationToken);
                return new(null, existing.Status == HoldPickupExpiryRunStatus.Succeeded
                    ? HoldPickupScheduledRunOutcome.AlreadySucceeded
                    : HoldPickupScheduledRunOutcome.InProgress);
            }
            throw;
        }
    }

    private async Task MarkFailedAsync(long runId, string message, CancellationToken cancellationToken)
    {
        await db.HoldPickupExpiryRuns.Where(run => run.Id == runId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(run => run.Status, HoldPickupExpiryRunStatus.Failed)
                .SetProperty(run => run.CompletedAtUtc, timeProvider.GetUtcNow().UtcDateTime)
                .SetProperty(run => run.ErrorMessage, message)
                .SetProperty(run => run.ResultMessage, message), cancellationToken);
        db.ChangeTracker.Clear();
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    private sealed record RunAcquisition(long? RunId, HoldPickupScheduledRunOutcome Outcome);
}
