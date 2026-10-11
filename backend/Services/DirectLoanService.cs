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
        int? readerId = null;
        if (model.CardCode.Length > 0)
        {
            var card = await db.LibraryCards.AsNoTracking().Include(c => c.ReaderAccount)
                .Include(c => c.LibraryCardType).SingleOrDefaultAsync(c => c.CardCode == model.CardCode, ct);
            if (card?.ReaderAccount == null || card.LibraryCardType == null)
                model.ReaderError = "Không tìm thấy mã thẻ hoặc hồ sơ bạn đọc hợp lệ.";
            else
            {
                var reader = card.ReaderAccount;
                readerId = reader.Id;
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
            if (copy?.Book == null) model.CopyError = "Mã vạch không tồn tại trong hệ thống.";
            else
            {
                model.BookTitle = copy.Book.Title;
                model.CopyStatus = copy.Status;
                var now = DateTime.UtcNow;
                // Read reservations even if the displayed copy status is inconsistent.
                var holds = await db.BookHolds.AsNoTracking()
                    .Where(h => h.BookCopyId == copy.Id && BookHoldStatus.WaitingPickupStatuses.Contains(h.Status)
                        && (h.PickupDeadlineUtc == null || h.PickupDeadlineUtc >= now))
                    .ToListAsync(ct);
                var ownHold = false;
                if (holds.Count > 1)
                    model.CopyError = "Có nhiều đơn đặt trước cùng giữ bản sao. Vui lòng kiểm tra dữ liệu trước khi cho mượn.";
                else if (holds.Count == 1)
                {
                    var hold = holds[0];
                    model.HoldId = hold.Id;
                    if (hold.ReaderAccountId != readerId)
                        model.CopyError = $"Bản sao đang được giữ cho đơn đặt trước #{hold.Id} của bạn đọc khác. Không thể cho mượn.";
                    else if (hold.BookId != copy.BookId || copy.Status != BookCopyStatus.OnHold)
                        model.CopyError = $"Dữ liệu bản sao và đơn đặt trước #{hold.Id} không nhất quán. Vui lòng kiểm tra lại.";
                    else ownHold = true;
                }
                if (model.CopyError == null && !ownHold && copy.Status != BookCopyStatus.Available)
                    model.CopyError = copy.Status switch
                    {
                        BookCopyStatus.OnLoan => "Bản sao này đang được mượn, không thể tiếp tục cho mượn.",
                        BookCopyStatus.UnderRepair => "Bản sao đang sửa chữa, chưa thể cho mượn.",
                        BookCopyStatus.Removed => "Bản sao đã bị loại khỏi kho, không thể cho mượn.",
                        BookCopyStatus.OnHold => "Bản sao đang được giữ cho đặt trước, chưa đủ điều kiện cho mượn.",
                        _ => $"Bản sao ở trạng thái {copy.Status}, không thể cho mượn trực tiếp."
                    };
                if (model.CopyError == null && await db.BookLoans.AnyAsync(l => l.BookCopyId == copy.Id, ct))
                    model.CopyError = "Bản sao đã có phiếu mượn. Vui lòng kiểm tra lại dữ liệu bản sao.";
            }
        }
        return model;
    }

    public Task<BookLoan?> ReceiptAsync(long id, CancellationToken ct = default) =>
        db.BookLoans.AsNoTracking().Include(l => l.BookCopy).Include(l => l.ReaderAccount)
            .FirstOrDefaultAsync(l => l.Id == id && l.BookCopyId != null, ct);

    public async Task<List<DirectLoanSelection>> ValidateRowsAsync(IReadOnlyList<string> codes,
        IReadOnlyList<string>? rowIds = null, CancellationToken ct = default, string? cardCode = null)
    {
        var rows = new List<DirectLoanSelection>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenCopies = new HashSet<long>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < codes.Count; i++)
        {
            var code = codes[i]?.Trim() ?? "";
            var id = rowIds != null && i < rowIds.Count ? rowIds[i] : Guid.NewGuid().ToString("N");
            var badId = string.IsNullOrWhiteSpace(id) || id.Length > 64 || !seenIds.Add(id);
            var lookup = code.Length is > 0 and <= 50 ? await LookupAsync(cardCode, code, ct) : new DirectLoanViewModel();
            var error = code.Length == 0 ? "Vui lòng nhập mã vạch bản sao."
                : code.Length > 50 ? "Mã vạch không được vượt quá 50 ký tự." : lookup.CopyError;
            if (!seenCodes.Add(code)) error = "Mã vạch này đã có trong danh sách mượn.";
            // Resolve database collation aliases as well as textual duplicates.
            if (lookup.BookTitle != null)
            {
                var copyId = await db.BookCopies.Where(c => c.CopyCode == code).Select(c => c.Id).SingleAsync(ct);
                if (!seenCopies.Add(copyId)) error = "Mã vạch này đã có trong danh sách mượn.";
            }
            if (badId) error = "Định danh dòng không hợp lệ. Vui lòng xóa dòng và nhập lại.";
            rows.Add(new(code, lookup.BookTitle ?? "Không tìm thấy", lookup.CopyStatus ?? "Không xác định")
            { RowId = id ?? "", Error = error, HoldId = lookup.HoldId });
        }
        return rows;
    }

    public async Task<BookLoanOutcome> ConfirmAsync(string cardCode, string copyCode, AdminAccount staff, CancellationToken ct = default, string? ipAddress = null)
    {
        var result = await ConfirmManyAsync(cardCode, [copyCode], staff, ct, ipAddress);
        return new(result.IsSuccess, result.ErrorMessage, result.Loans?.FirstOrDefault());
    }

    public async Task<DirectLoanBatchOutcome> ConfirmManyAsync(string cardCode, IReadOnlyList<string> copyCodes,
        AdminAccount staff, CancellationToken ct = default, string? ipAddress = null, IReadOnlyList<string>? rowIds = null)
    {
        if (string.IsNullOrWhiteSpace(cardCode) || copyCodes.Count == 0)
            return new(false, "Vui lòng nhập mã thẻ và chọn ít nhất một bản sao.");
        var codes = copyCodes.Select(c => c?.Trim() ?? "").ToArray();
        if (cardCode.Trim().Length > 32 || (rowIds != null && rowIds.Count != codes.Length))
            return new(false, "Mã thẻ hoặc mã vạch không hợp lệ.");
        // A serializable transaction includes the existing loan service's SaveChanges.
        // SQL Server update locks serialize requests for the same reader before checking their limit.
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var normalizedCard = cardCode.Trim();
            if (db.Database.IsSqlServer())
            {
                await db.ReaderAccounts.FromSqlInterpolated($"SELECT r.* FROM ReaderAccounts r WITH (UPDLOCK, HOLDLOCK) INNER JOIN LibraryCards c ON c.ReaderAccountId = r.Id WHERE c.CardCode = {normalizedCard}")
                    .AsNoTracking().ToListAsync(ct);
            }
            var page = await LookupAsync(normalizedCard, null, ct);
            var rows = await ValidateRowsAsync(codes, rowIds, ct, normalizedCard);
            if (page.ReaderName == null || page.ReaderError != null)
                return new(false, page.ReaderError ?? "Thông tin bạn đọc không hợp lệ.", Rows: rows);
            if (rows.Any(r => !r.IsValid))
                return new(false, rows.First(r => !r.IsValid).Error, Rows: rows);
            if (codes.Length > page.RemainingLoans)
                return new(false, $"Đã chọn {codes.Length} bản sao, nhưng bạn đọc chỉ còn được mượn thêm {page.RemainingLoans} sách.", Rows: rows);
            var copies = new List<BookCopy>();
            foreach (var code in codes)
            {
                copies.Add(await db.BookCopies.AsNoTracking().SingleAsync(c => c.CopyCode == code, ct));
            }
            if (copies.Select(c => c.Id).Distinct().Count() != copies.Count)
                return new(false, "Mã vạch này đã có trong danh sách mượn.");
            var card = await db.LibraryCards.AsNoTracking().Include(c => c.LibraryCardType).SingleAsync(c => c.CardCode == normalizedCard, ct);
            var loanDate = DateOnly.FromDateTime(DateTime.Today);
            var originalDueDate = loanDate.AddDays(card.LibraryCardType!.LoanDays!.Value);
            DateOnly dueDate;
            try { dueDate = await schedules.AdjustLoanDueDateAsync(originalDueDate, ct); }
            catch (InvalidOperationException ex) { return new(false, ex.Message); }

            var created = new List<BookLoan>();
            // Stable ordering reduces lock conflicts for overlapping batches.
            foreach (var copy in copies.OrderBy(c => c.Id))
            {
                var index = copies.FindIndex(c => c.Id == copy.Id);
                var holdId = rows[index].HoldId;
                if (holdId.HasValue)
                {
                    var now = DateTime.UtcNow;
                    var claimed = await db.BookHolds.Where(h => h.Id == holdId.Value && h.BookCopyId == copy.Id
                        && h.BookId == copy.BookId && h.ReaderAccountId == card.ReaderAccountId
                        && BookHoldStatus.WaitingPickupStatuses.Contains(h.Status)
                        && (h.PickupDeadlineUtc == null || h.PickupDeadlineUtc >= now))
                        .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, BookHoldStatus.ConvertedToLoan), ct);
                    if (claimed != 1)
                    {
                        rows[index] = rows[index] with { Error = $"Đơn đặt trước #{holdId} không còn hiệu lực hoặc quyền giữ đã thay đổi. Vui lòng kiểm tra lại." };
                        return new(false, "Vui lòng xử lý dòng lỗi trước khi xác nhận.", Rows: rows);
                    }
                }
                var expectedStatus = holdId.HasValue ? BookCopyStatus.OnHold : BookCopyStatus.Available;
                var changed = await db.BookCopies.Where(c => c.Id == copy.Id && c.Status == expectedStatus)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.Status, BookCopyStatus.OnLoan)
                        .SetProperty(c => c.StatusReason, "Cho mượn trực tiếp tại quầy"), ct);
                if (changed != 1)
                {
                    rows[index] = rows[index] with { Error = "Bản sao không còn sẵn sàng. Vui lòng kiểm tra lại." };
                    return new(false, "Vui lòng xử lý dòng lỗi trước khi xác nhận.", Rows: rows);
                }
                // Reuse existing eligibility rules and due dates inside the same transaction.
                var result = await loans.CreateForHoldAsync(copy.BookId, card.ReaderAccountId, loanDate,
                    originalDueDate, dueDate, staff.Email, ct);
                if (!result.IsSuccess || result.Loan == null) return new(false, result.ErrorMessage);
                result.Loan.BookCopyId = copy.Id;
                result.Loan.SourceBookHoldId = holdId;
                result.Loan.CreatedByAdminAccountId = staff.Id;
                db.BookCopyStatusHistories.Add(new BookCopyStatusHistory
                {
                    BookCopyId = copy.Id, FromStatus = expectedStatus, ToStatus = BookCopyStatus.OnLoan,
                    Reason = $"Cho mượn trực tiếp tại quầy – phiếu #{result.Loan.Id}", ChangedBy = staff.Email, ChangedAtUtc = DateTime.UtcNow
                });
                db.AuditLogs.Add(AuditLogService.Create(staff.Email, AuditActions.CreateLoan,
                    $"Cho mượn trực tiếp – phiếu #{result.Loan.Id}, bạn đọc #{card.ReaderAccountId}, bản sao {copy.CopyCode}", ipAddress));
                await db.SaveChangesAsync(ct);
                created.Add(result.Loan);
            }
            await transaction.CommitAsync(ct);
            return new(true, Loans: copies.Select(c => created.Single(l => l.BookCopyId == c.Id)).ToList());
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException
            or InvalidOperationException { InnerException: DbException })
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
