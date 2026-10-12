using System.Data;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed class HoldPickupConfirmationService(
    ApplicationDbContext db,
    IBookLoanService loanService,
    IWorkingScheduleService workingScheduleService,
    IBookHoldFulfillmentService? holdFulfillmentService = null)
    : IHoldPickupConfirmationService
{
    private readonly IBookHoldFulfillmentService holdFulfillment =
        holdFulfillmentService ?? new BookHoldFulfillmentService(db, loanService);

    public async Task<HoldPickupConfirmationResult> ConfirmAsync(
        long holdId, string libraryCardCode, string actor, int? createdByAdminAccountId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var hold = await db.BookHolds
                .Include(item => item.ReaderAccount).ThenInclude(reader => reader!.LibraryCard)
                    .ThenInclude(card => card!.LibraryCardType)
                .Include(item => item.BookCopy)
                .FirstOrDefaultAsync(item => item.Id == holdId, cancellationToken);
            if (hold == null)
                return await RejectAsync(transaction, "Không tìm thấy đơn đặt giữ.", cancellationToken);

            var now = DateTime.UtcNow;
            if (hold.Status == BookHoldStatus.Cancelled && hold.CancellationReason == BookHoldStatus.ExpiredCancellationReason)
                return await RejectAsync(transaction, ExpiredMessage, cancellationToken);

            if (!HoldPickupService.WaitingPickupStatuses.Contains(hold.Status))
            {
                var message = hold.Status == BookHoldStatus.ConvertedToLoan
                    ? "Đơn đặt giữ này đã được chuyển thành phiếu mượn."
                    : "Đơn đặt giữ không còn ở trạng thái chờ nhận.";
                return await RejectAsync(transaction, message, cancellationToken);
            }

            if (hold.PickupDeadlineUtc is { } pickupDeadline && pickupDeadline < now)
                return await RejectAsync(transaction, ExpiredMessage, cancellationToken);

            if (hold.BookCopyId == null || hold.BookCopy == null)
                return await RejectAsync(transaction, "Đơn đặt giữ chưa được gán bản sao sách.", cancellationToken);

            if (hold.ReaderAccount?.LibraryCard == null ||
                !string.Equals(hold.ReaderAccount.LibraryCard.CardCode, libraryCardCode?.Trim(), StringComparison.Ordinal))
                return await RejectAsync(transaction, "Mã thẻ bạn đọc không khớp với đơn đặt giữ.", cancellationToken);

            if (hold.BookCopy.BookId != hold.BookId || hold.BookCopy.Status != BookCopyStatus.OnHold)
                return await RejectAsync(transaction, "Bản sao được giữ không còn ở trạng thái hợp lệ để cho mượn.", cancellationToken);

            var cardType = hold.ReaderAccount.LibraryCard.LibraryCardType;
            if (cardType?.LoanDays is not > 0)
                return await RejectAsync(transaction,
                    "Loại thẻ của bạn đọc chưa được cấu hình số ngày mượn. Vui lòng cập nhật tại Chính sách mượn.", cancellationToken);

            // Match the application's existing DateTime.Today / ToLocalTime calendar-day convention.
            var loanDate = DateOnly.FromDateTime(now.ToLocalTime());
            var originalDueDate = loanDate.AddDays(cardType.LoanDays.Value);
            DateOnly dueDate;
            try
            {
                dueDate = await workingScheduleService.AdjustLoanDueDateAsync(originalDueDate, cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                return await RejectAsync(transaction, exception.Message, cancellationToken);
            }

            var loanResult = await loanService.CreateForHoldAsync(
                hold.BookId, hold.ReaderAccountId, loanDate, originalDueDate, dueDate, actor, cancellationToken);
            if (!loanResult.IsSuccess || loanResult.Loan == null)
                return await RejectAsync(transaction, loanResult.ErrorMessage ?? "Không thể tạo phiếu mượn.", cancellationToken);

            var copy = hold.BookCopy;
            copy.Status = BookCopyStatus.OnLoan;
            copy.StatusReason = $"Issued for book hold #{hold.Id}";
            loanResult.Loan.BookCopyId = copy.Id;
            loanResult.Loan.SourceBookHoldId = hold.Id;
            loanResult.Loan.CreatedByAdminAccountId = createdByAdminAccountId;
            hold.Status = BookHoldStatus.ConvertedToLoan;

            db.BookCopyStatusHistories.Add(new BookCopyStatusHistory
            {
                BookCopyId = copy.Id,
                FromStatus = BookCopyStatus.OnHold,
                ToStatus = BookCopyStatus.OnLoan,
                Reason = $"Issued for book hold #{hold.Id}",
                ChangedBy = string.IsNullOrWhiteSpace(actor) ? "Thủ thư" : actor,
                ChangedAtUtc = now
            });

            await db.SaveChangesAsync(cancellationToken);
            await holdFulfillment.FulfillNextAsync(hold.BookId, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(true, $"Đã xác nhận đơn đặt giữ và tạo phiếu mượn #{loanResult.Loan.Id}.", loanResult.Loan.Id);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private const string ExpiredMessage = "Đơn đặt giữ đã quá hạn nhận. Vui lòng yêu cầu bạn đọc đặt lại đơn.";

    private static async Task<HoldPickupConfirmationResult> RejectAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        string message, CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);
        return new(false, message);
    }
}
