using System.Data;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed class BookHoldFulfillmentService(ApplicationDbContext db, IBookLoanService loanService)
    : IBookHoldFulfillmentService
{
    private const string SystemActor = "System: book hold fulfillment";

    public async Task<BookHoldFulfillmentResult> FulfillNextAsync(
        int bookId, CancellationToken cancellationToken = default)
    {
        var ownsTransaction = db.Database.CurrentTransaction == null;
        await using var transaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

        try
        {
            var firstActiveHold = await db.BookHolds
                .Where(hold => hold.BookId == bookId &&
                    (hold.Status == BookHoldStatus.Waiting || HoldPickupService.WaitingPickupStatuses.Contains(hold.Status)))
                .OrderBy(hold => hold.HeldAtUtc)
                .ThenBy(hold => hold.Id)
                .FirstOrDefaultAsync(cancellationToken);

            // A hold already attached to a copy occupies the processing slot. Legacy L1 promotion can
            // also mark the head as awaiting pickup before a copy exists; keep it at the head until
            // inventory arrives, then attach that copy without skipping to a later reader.
            var headNeedsCopy = firstActiveHold != null && firstActiveHold.BookCopyId == null &&
                (firstActiveHold.Status == BookHoldStatus.Waiting ||
                 HoldPickupService.WaitingPickupStatuses.Contains(firstActiveHold.Status));
            if (!headNeedsCopy)
            {
                if (transaction != null) await transaction.CommitAsync(cancellationToken);
                return new(false);
            }
            var head = firstActiveHold!;

            var copy = await db.BookCopies
                .Where(item => item.BookId == bookId && item.Status == BookCopyStatus.Available)
                .OrderBy(item => item.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (copy == null)
            {
                if (transaction != null) await transaction.CommitAsync(cancellationToken);
                return new(false);
            }

            var reservedAtUtc = DateTime.UtcNow;
            DateTime deadlineUtc;
            try { deadlineUtc = await HoldPickupDeadlineCalculator.ThreeOpenDaysAfterAsync(reservedAtUtc, loanService, cancellationToken); }
            catch (InvalidOperationException exception)
            {
                if (transaction != null) await transaction.CommitAsync(cancellationToken);
                return new(false, ErrorMessage: exception.Message);
            }

            copy.Status = BookCopyStatus.OnHold;
            copy.StatusReason = $"Held for book hold #{head.Id}";
            head.BookCopyId = copy.Id;
            head.Status = BookHoldStatus.Available;
            head.PickupDeadlineUtc = deadlineUtc;
            db.BookCopyStatusHistories.Add(new BookCopyStatusHistory
            {
                BookCopyId = copy.Id,
                FromStatus = BookCopyStatus.Available,
                ToStatus = BookCopyStatus.OnHold,
                Reason = $"Automatically held for book hold #{head.Id}",
                ChangedBy = SystemActor,
                ChangedAtUtc = reservedAtUtc
            });

            await db.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
            return new(true, head.Id, copy.Id, copy.CopyCode, deadlineUtc);
        }
        catch
        {
            if (transaction != null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
