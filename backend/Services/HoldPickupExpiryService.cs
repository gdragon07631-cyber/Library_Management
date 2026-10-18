using System.Data;
using System.Security.Cryptography;
using System.Text;
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
        var releasedCopiesByBook = new Dictionary<int, List<ExpiredCopy>>();
        foreach (var holdId in overdueIds)
        {
            try
            {
                var result = await ExpireOneAsync(holdId, nowUtc, cancellationToken);
                if (result == null) continue;
                expired++;
                if (result.ReleasedCopy)
                {
                    if (!releasedCopiesByBook.TryGetValue(result.BookId, out var copies))
                        releasedCopiesByBook[result.BookId] = copies = [];
                    copies.Add(new(result.HoldId, result.BookCopyId!.Value));
                }
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
        foreach (var (bookId, releasedCopies) in releasedCopiesByBook)
        {
            for (var index = 0; index < releasedCopies.Count; index++)
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
                    available += releasedCopies.Count - index;
                    break;
                }
                finally
                {
                    db.ChangeTracker.Clear();
                }
            }
        }

        foreach (var expiredCopy in releasedCopiesByBook.Values.SelectMany(copies => copies))
        {
            try
            {
                var copyStatus = await db.BookCopies.AsNoTracking()
                    .Where(copy => copy.Id == expiredCopy.BookCopyId)
                    .Select(copy => copy.Status)
                    .SingleOrDefaultAsync(cancellationToken);
                var nextHold = copyStatus == BookCopyStatus.OnHold
                    ? await db.BookHolds.AsNoTracking()
                        .Where(hold => hold.BookCopyId == expiredCopy.BookCopyId &&
                            HoldPickupService.WaitingPickupStatuses.Contains(hold.Status))
                        .OrderByDescending(hold => hold.Id)
                        .Select(hold => new { hold.Id })
                        .FirstOrDefaultAsync(cancellationToken)
                    : null;
                var outcome = nextHold != null
                    ? BookHoldExpiryCopyOutcome.TransferredToNextReader
                    : copyStatus == BookCopyStatus.Available
                        ? BookHoldExpiryCopyOutcome.ReturnedToAvailable
                        : BookHoldExpiryCopyOutcome.Unknown;
                await db.BookHolds.Where(hold => hold.Id == expiredCopy.HoldId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(hold => hold.ExpiryCopyOutcome, outcome)
                        .SetProperty(hold => hold.ExpiryNextHoldId, nextHold == null ? null : nextHold.Id), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Failed to record copy outcome for expired hold {HoldId}.", expiredCopy.HoldId);
                errors.Add($"Hold #{expiredCopy.HoldId}: copy outcome could not be recorded.");
            }
            finally
            {
                db.ChangeTracker.Clear();
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
        var pickupDeadlineUtc = hold.PickupDeadlineUtc.Value;
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
        hold.ExpiredBookCopyId = releasedCopy?.Id;
        hold.ExpiredBookCopyCode = releasedCopy?.CopyCode;
        hold.ExpiryCopyOutcome = releasedCopy == null
            ? BookHoldExpiryCopyOutcome.NoCopyAttached
            : BookHoldExpiryCopyOutcome.Unknown;
        hold.ExpiryNextHoldId = null;
        var bookTitle = await db.Books.Where(book => book.Id == hold.BookId).Select(book => book.Title).SingleOrDefaultAsync(cancellationToken);
        var readerEmail = await db.ReaderAccounts.Where(reader => reader.Id == hold.ReaderAccountId)
            .Select(reader => reader.Email).SingleOrDefaultAsync(cancellationToken);
        var eventKey = $"hold-expired|{hold.Id}|{hold.CancelledAtUtc!.Value.Ticks}";
        db.EmailSendHistories.Add(new EmailSendHistory
        {
            DeduplicationKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(eventKey))),
            ReaderAccountId = hold.ReaderAccountId,
            HoldId = hold.Id,
            BookTitleSnapshot = bookTitle,
            PickupDeadlineUtcSnapshot = pickupDeadlineUtc,
            EventOccurredAtUtc = hold.CancelledAtUtc,
            Recipient = readerEmail?.Trim() ?? string.Empty,
            NotificationType = "HoldExpired",
            Subject = "Th\u00F4ng b\u00E1o \u0111\u01A1n \u0111\u1EB7t gi\u1EEF s\u00E1ch \u0111\u00E3 b\u1ECB h\u1EE7y do qu\u00E1 h\u1EA1n nh\u1EADn",
            StartedAtUtc = hold.CancelledAtUtc.Value,
            Status = "Queued"
        });
        db.AuditLogs.Add(AuditLogService.Create(
            "\u0048\u1EC7 th\u1ED1ng",
            AuditActions.CancelHold,
            $"Đơn đặt giữ #{hold.Id} \"{bookTitle}\" (sách #{hold.BookId}) của bạn đọc #{hold.ReaderAccountId} ({readerEmail}) – " +
                "tự hủy vì quá hạn nhận sách" + (releasedCopy != null ? $", trả bản sao {releasedCopy.CopyCode} về kệ" : string.Empty),
            AuditLogService.UnknownIpAddress));
        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new(hold.Id, hold.BookId, releasedCopy?.Id);
    }

    private sealed record ExpiryOutcome(long HoldId, int BookId, long? BookCopyId)
    {
        public bool ReleasedCopy => BookCopyId.HasValue;
    }

    private sealed record ExpiredCopy(long HoldId, long BookCopyId);
}
