using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed class DirectLoanService(ApplicationDbContext db, IBookLoanService loans,
    IWorkingScheduleService schedules, ILogger<DirectLoanService> logger)
{
    public async Task<DirectLoanViewModel> LookupAsync(string? cardCode, string? copyCode, CancellationToken ct = default)
    {
        var model = new DirectLoanViewModel { CardCode = cardCode?.Trim() ?? "", CopyCode = copyCode?.Trim() ?? "" };
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (model.CardCode.Length > 0)
        {
            var card = await db.LibraryCards.AsNoTracking().Include(c => c.ReaderAccount)
                .Include(c => c.LibraryCardType).SingleOrDefaultAsync(c => c.CardCode == model.CardCode, ct);
            if (card?.ReaderAccount == null || card.LibraryCardType == null)
                model.ReaderError = "Không tìm thấy mã thẻ hoặc hồ sơ bạn đọc hợp lệ.";
            else
            {
                var reader = card.ReaderAccount;
                var existing = await db.BookLoans.AsNoTracking().Where(l => l.ReaderAccountId == reader.Id).ToListAsync(ct);
                model.ReaderName = reader.FullName;
                model.CardTypeName = card.LibraryCardType.Name;
                model.MaxBooks = card.LibraryCardType.MaxBooks;
                model.CurrentLoans = existing.Count(l => !l.IsReturned);
                model.RemainingLoans = Math.Max(0, model.MaxBooks - model.CurrentLoans);
                if (reader.IsLocked || card.IsLocked || reader.Status != "Đang hoạt động" || card.Status != "Đang hoạt động")
                    model.ReaderError = "Thẻ hoặc tài khoản bạn đọc không ở trạng thái hoạt động.";
                else if (card.IssuedOn > today || card.ExpiresOn < today)
                    model.ReaderError = "Thẻ bạn đọc chưa có hiệu lực hoặc đã hết hạn.";
                else if (!card.LibraryCardType.IsActive)
                    model.ReaderError = "Loại thẻ đã ngừng sử dụng.";
                else if (card.LibraryCardType.LoanDays is not > 0)
                    model.ReaderError = "Loại thẻ chưa được cấu hình số ngày mượn. Vui lòng cập nhật tại Chính sách mượn.";
                else if (existing.Any(l => !l.IsReturned && l.DueDate < today))
                    model.ReaderError = "Bạn đọc đang có phiếu mượn quá hạn chưa trả.";
                else if (reader.TotalDebt > 0)
                    model.ReaderError = "Bạn đọc còn phí/phạt chưa thanh toán, không thể mượn sách.";
                else if (model.RemainingLoans == 0)
                    model.ReaderError = $"Bạn đọc đã đạt giới hạn mượn {model.CurrentLoans}/{model.MaxBooks} sách.";
            }
        }
        if (model.CopyCode.Length > 0)
        {
            var copy = await db.BookCopies.AsNoTracking().Include(c => c.Book).SingleOrDefaultAsync(c => c.CopyCode == model.CopyCode, ct);
            if (copy?.Book == null) model.CopyError = "Không tìm thấy mã vạch bản sao.";
            else
            {
                model.BookTitle = copy.Book.Title;
                model.CopyStatus = copy.Status;
                if (copy.Status != BookCopyStatus.Available)
                    model.CopyError = $"Bản sao ở trạng thái {copy.Status}, không thể cho mượn trực tiếp.";
                else if (await db.BookLoans.AnyAsync(l => l.BookCopyId == copy.Id, ct))
                    model.CopyError = "Bản sao đã có phiếu mượn. Vui lòng kiểm tra lại dữ liệu bản sao.";
            }
        }
        return model;
    }

    public Task<BookLoan?> ReceiptAsync(long id, CancellationToken ct = default) =>
        db.BookLoans.AsNoTracking().Include(l => l.BookCopy).Include(l => l.ReaderAccount)
            .FirstOrDefaultAsync(l => l.Id == id && l.BookCopyId != null && l.SourceBookHoldId == null, ct);

    public async Task<BookLoanOutcome> ConfirmAsync(string cardCode, string copyCode, AdminAccount staff, CancellationToken ct = default, string? ipAddress = null)
    {
        if (string.IsNullOrWhiteSpace(cardCode) || string.IsNullOrWhiteSpace(copyCode))
            return new(false, "Vui lòng nhập mã thẻ và một mã vạch bản sao.");
        // A serializable transaction includes the existing loan service's SaveChanges.
        // SQL Server update locks serialize requests for the same reader before checking their limit.
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var normalizedCard = cardCode.Trim();
            var normalizedCopy = copyCode.Trim();
            if (db.Database.IsSqlServer())
            {
                await db.ReaderAccounts.FromSqlInterpolated($"SELECT r.* FROM ReaderAccounts r WITH (UPDLOCK, HOLDLOCK) INNER JOIN LibraryCards c ON c.ReaderAccountId = r.Id WHERE c.CardCode = {normalizedCard}")
                    .AsNoTracking().ToListAsync(ct);
            }
            var page = await LookupAsync(normalizedCard, normalizedCopy, ct);
            if (!page.CanConfirm) return new(false, page.ReaderError ?? page.CopyError ?? "Thông tin mượn không hợp lệ.");
            var card = await db.LibraryCards.Include(c => c.LibraryCardType).SingleAsync(c => c.CardCode == normalizedCard, ct);
            var copy = await db.BookCopies.AsNoTracking().SingleAsync(c => c.CopyCode == normalizedCopy, ct);
            // Atomic state transition also protects against another workflow issuing this copy.
            var changed = await db.BookCopies.Where(c => c.Id == copy.Id && c.Status == BookCopyStatus.Available)
                .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.Status, BookCopyStatus.OnLoan)
                    .SetProperty(c => c.StatusReason, "Cho mượn trực tiếp tại quầy"), ct);
            if (changed != 1) return new(false, "Bản sao không còn sẵn sàng. Vui lòng tra cứu lại.");
            var loanDate = DateOnly.FromDateTime(DateTime.Today);
            var originalDueDate = loanDate.AddDays(card.LibraryCardType!.LoanDays!.Value);
            DateOnly dueDate;
            try { dueDate = await schedules.AdjustLoanDueDateAsync(originalDueDate, ct); }
            catch (InvalidOperationException ex) { return new(false, ex.Message); }

            // Reuse all existing eligibility rules, without bypass privileges or new defaults.
            var result = await loans.CreateForHoldAsync(copy.BookId, card.ReaderAccountId, loanDate,
                originalDueDate, dueDate, staff.Email, ct);
            if (!result.IsSuccess || result.Loan == null) return result;
            result.Loan.BookCopyId = copy.Id;
            result.Loan.CreatedByAdminAccountId = staff.Id;
            db.BookCopyStatusHistories.Add(new BookCopyStatusHistory
            {
                BookCopyId = copy.Id, FromStatus = BookCopyStatus.Available, ToStatus = BookCopyStatus.OnLoan,
                Reason = $"Cho mượn trực tiếp tại quầy – phiếu #{result.Loan.Id}", ChangedBy = staff.Email, ChangedAtUtc = DateTime.UtcNow
            });
            db.AuditLogs.Add(AuditLogService.Create(staff.Email, AuditActions.CreateLoan,
                $"Cho mượn trực tiếp – phiếu #{result.Loan.Id}, bạn đọc #{card.ReaderAccountId}, bản sao {copy.CopyCode}", ipAddress));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException)
        {
            logger.LogWarning(ex, "Không thể lưu lượt mượn trực tiếp.");
            return new(false, "Không thể lưu phiếu mượn. Dữ liệu chưa được ghi nhận; vui lòng tra cứu lại và thử lại.");
        }
        finally
        {
            // Disposal rolls back an uncommitted transaction; clear tracked inserts/changes so a later save cannot replay them.
            db.ChangeTracker.Clear();
        }
    }
}
