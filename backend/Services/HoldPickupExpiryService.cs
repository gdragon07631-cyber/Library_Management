using System.Data;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed class HoldPickupExpiryService(
    ApplicationDbContext db,
    IBookHoldFulfillmentService holdFulfillmentService,
    ILogger<HoldPickupExpiryService> logger) : IHoldPickupExpiryService
{
    public const string ExpiredReason = BookHoldStatus.ExpiredCancellationReason;
    private const string SystemActor = "System: hold pickup expiry";

    public async Task<int> ExpireOverdueAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
        => (await ProcessOverdueAsync(nowUtc, cancellationToken)).CancelledCount;

    public async Task<HoldPickupExpiryRunResult> ProcessOverdueAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var overdueIds = await db.BookHolds
            .Where(hold => HoldPickupService.WaitingPickupStatuses.Contains(hold.Status) &&
                hold.PickupDeadlineUtc != null && hold.PickupDeadlineUtc < nowUtc)
            .OrderBy(hold => hold.PickupDeadlineUtc)
            .Select(hold => hold.Id)
            .ToListAsync(cancellationToken);

        var expired = 0;
        var assigned = 0;
        var available = 0;
        var errors = new List<string>();
        var releasedCopiesByBook = new Dictionary<int, int>();
        foreach (var holdId in overdueIds)
        {
            try
            {
                var result = await ExpireOneAsync(holdId, nowUtc, cancellationToken);
                if (result == null) continue;
                expired++;
                if (result.ReleasedCopy)
                    releasedCopiesByBook[result.BookId] = releasedCopiesByBook.GetValueOrDefault(result.BookId) + 1;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Failed to expire overdue hold {HoldId}.", holdId);
                errors.Add($"Hold #{holdId}: processing failed due to a system error.");
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        // Cancel every expired pickup before assigning released copies so an expired request
        // later in the queue cannot receive a fresh pickup deadline during this same run.
        foreach (var (bookId, releasedCount) in releasedCopiesByBook)
        {
            for (var index = 0; index < releasedCount; index++)
            {
                try
                {
                    var fulfillment = await holdFulfillmentService.FulfillNextAsync(bookId, cancellationToken);
                    if (fulfillment.IsAssigned) assigned++;
                    else available++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Failed to reassign a released copy for book {BookId}.", bookId);
                    errors.Add($"Book #{bookId}: a released copy could not be reassigned.");
                    available += releasedCount - index;
                    break;
                }
                finally
                {
                    db.ChangeTracker.Clear();
                }
            }
        }

        if (expired > 0) logger.LogInformation("Expired {Count} overdue book holds.", expired);
        return new(overdueIds.Count, expired, assigned, available, errors);
    }

    private async Task<ExpiryOutcome?> ExpireOneAsync(long holdId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var hold = await db.BookHolds.Include(item => item.BookCopy)
            .SingleOrDefaultAsync(item => item.Id == holdId, cancellationToken);
        if (hold == null || !HoldPickupService.WaitingPickupStatuses.Contains(hold.Status) ||
            hold.PickupDeadlineUtc == null || hold.PickupDeadlineUtc >= nowUtc)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return null;
        }

        var releasedCopy = hold.BookCopy;
        if (releasedCopy != null)
        {
            var previousStatus = releasedCopy.Status;
            releasedCopy.Status = BookCopyStatus.Available;
            releasedCopy.StatusReason = null;
            db.BookCopyStatusHistories.Add(new BookCopyStatusHistory
            {
                BookCopyId = releasedCopy.Id,
                FromStatus = previousStatus,
                ToStatus = BookCopyStatus.Available,
                Reason = $"Released after hold #{hold.Id} passed its pickup deadline",
                ChangedBy = SystemActor,
                ChangedAtUtc = nowUtc
            });
        }

        hold.BookCopyId = null;
        hold.PickupDeadlineUtc = null;
        hold.Status = BookHoldStatus.Cancelled;
        hold.CancellationReason = ExpiredReason;
        hold.CancelledAtUtc = nowUtc;
        var bookTitle = await db.Books.Where(book => book.Id == hold.BookId).Select(book => book.Title).SingleOrDefaultAsync(cancellationToken);
        var readerEmail = await db.ReaderAccounts.Where(reader => reader.Id == hold.ReaderAccountId)
            .Select(reader => reader.Email).SingleOrDefaultAsync(cancellationToken);
        db.AuditLogs.Add(AuditLogService.Create(
            "\u0048\u1EC7 th\u1ED1ng",
            AuditActions.CancelHold,
            $"Đơn đặt giữ #{hold.Id} \"{bookTitle}\" (sách #{hold.BookId}) của bạn đọc #{hold.ReaderAccountId} ({readerEmail}) – " +
                "tự hủy vì quá hạn nhận sách" + (releasedCopy != null ? $", trả bản sao {releasedCopy.CopyCode} về kệ" : string.Empty),
            AuditLogService.UnknownIpAddress));
        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new(hold.BookId, releasedCopy != null);
    }

    private sealed record ExpiryOutcome(int BookId, bool ReleasedCopy);
}
