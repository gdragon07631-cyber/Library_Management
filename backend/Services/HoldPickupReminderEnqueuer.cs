using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed class HoldPickupReminderEnqueuer(
    ApplicationDbContext db,
    TimeProvider timeProvider,
    ILogger<HoldPickupReminderEnqueuer> logger)
{
    public async Task<int> EnqueueForDateAsync(DateOnly businessDate, CancellationToken cancellationToken = default)
    {
        var startUtc = HoldPickupExpirySchedule.ToUtc(businessDate, TimeOnly.MinValue);
        var endUtc = HoldPickupExpirySchedule.ToUtc(businessDate.AddDays(1), TimeOnly.MinValue);
        var dueHolds = await db.BookHolds.AsNoTracking()
            .Include(hold => hold.ReaderAccount)
            .Include(hold => hold.Book)
            .Include(hold => hold.BookCopy).ThenInclude(copy => copy!.Shelf).ThenInclude(shelf => shelf!.Warehouse)
            .Where(hold => HoldPickupService.WaitingPickupStatuses.Contains(hold.Status) &&
                hold.BookCopyId != null && hold.PickupDeadlineUtc >= startUtc && hold.PickupDeadlineUtc < endUtc)
            .OrderBy(hold => hold.Id)
            .ToListAsync(cancellationToken);

        var queued = 0;
        foreach (var hold in dueHolds)
        {
            var key = CreateDeduplicationKey(hold.Id, businessDate);
            try
            {
                if (await db.EmailSendHistories.AsNoTracking().AnyAsync(
                    item => item.DeduplicationKey == key, cancellationToken)) continue;

                var now = timeProvider.GetUtcNow().UtcDateTime;
                db.EmailSendHistories.Add(new EmailSendHistory
                {
                    DeduplicationKey = key,
                    ReaderAccountId = hold.ReaderAccountId,
                    HoldId = hold.Id,
                    Recipient = hold.ReaderAccount?.Email?.Trim() ?? string.Empty,
                    NotificationType = "HoldPickupLastDay",
                    Subject = "Nhắc nhở: Hôm nay là hạn cuối nhận sách đã đặt giữ",
                    StartedAtUtc = now,
                    Status = "Queued"
                });
                await db.SaveChangesAsync(cancellationToken);
                queued++;
                logger.LogInformation("Queued last-day pickup reminder for hold {HoldId} on Vietnam date {BusinessDate}.", hold.Id, businessDate);
            }
            catch (DbUpdateException exception)
            {
                db.ChangeTracker.Clear();
                if (await db.EmailSendHistories.AsNoTracking().AnyAsync(item => item.DeduplicationKey == key, cancellationToken))
                    continue;
                logger.LogError(exception, "Could not queue last-day pickup reminder for hold {HoldId}.", hold.Id);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                logger.LogError(exception, "Could not queue last-day pickup reminder for hold {HoldId}; other holds will still be checked.", hold.Id);
            }
        }

        return queued;
    }

    public static string CreateDeduplicationKey(long holdId, DateOnly businessDate) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"hold-last-day|{holdId}|{businessDate:yyyy-MM-dd}")));
}
