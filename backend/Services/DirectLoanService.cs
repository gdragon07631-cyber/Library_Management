using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed partial class DirectLoanService(ApplicationDbContext db, IBookLoanService loans,
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
                model.ReaderAccountId = reader.Id;
                var existing = await db.BookLoans.AsNoTracking().Where(l => l.ReaderAccountId == reader.Id).ToListAsync(ct);
                model.ReaderName = reader.FullName;
                model.CardTypeName = card.LibraryCardType.Name;
                model.MaxBooks = card.LibraryCardType.MaxBooks;
                model.CurrentLoans = existing.Count(l => !l.IsReturned);
                model.RemainingLoans = Math.Max(0, model.MaxBooks - model.CurrentLoans);
                model.OutstandingDebt = reader.TotalDebt;
                if (reader.IsLocked || LibraryCardStatusHelper.IsCardLocked(card, DateTime.UtcNow) || card.Status.Contains("khóa", StringComparison.OrdinalIgnoreCase)
                    || card.Status.Contains("khoá", StringComparison.OrdinalIgnoreCase))
                {
                    model.ReaderError = LibraryCardStatusHelper.IsCardLocked(card, DateTime.UtcNow)
                        || card.Status.Contains("khóa", StringComparison.OrdinalIgnoreCase)
                        || card.Status.Contains("khoá", StringComparison.OrdinalIgnoreCase)
                        ? LibraryCardStatusHelper.GetLockMessage(card, DateTime.UtcNow) ?? "Thẻ bạn đọc đang bị khóa."
                        : "Tài khoản bạn đọc đang bị khóa.";
                    model.CanOverrideBlock = true;
                }
                else if (reader.Status != "Đang hoạt động" || card.Status != "Đang hoạt động")
                    model.ReaderError = "Thẻ hoặc tài khoản bạn đọc không ở trạng thái hoạt động.";
                else if (card.IssuedOn > today || card.ExpiresOn < today)
                {
                    model.ReaderError = "Thẻ bạn đọc chưa có hiệu lực hoặc đã hết hạn.";
                    model.CanOverrideBlock = true;
                }
                else if (!card.LibraryCardType.IsActive)
                    model.ReaderError = "Loại thẻ đã ngừng sử dụng.";
                else if (card.LibraryCardType.LoanDays is not > 0)
                    model.ReaderError = "Loại thẻ chưa được cấu hình số ngày mượn. Vui lòng cập nhật tại Chính sách mượn.";
                else if (existing.Any(l => !l.IsReturned && l.DueDate < today))
                {
                    model.ReaderError = "Bạn đọc đang có phiếu mượn quá hạn chưa trả.";
                    model.CanOverrideBlock = true;
                }
                else if (model.OutstandingDebt > 0)
                {
                    model.ReaderError = $"Bạn đọc còn nợ {BookLoanService.FormatVnd(model.OutstandingDebt)} phí chưa thanh toán, không thể mượn sách.";
                    model.CanOverrideBlock = true;
                }
                else if (model.RemainingLoans == 0)
                {
                    model.ReaderError = $"Bạn đọc đã đạt giới hạn mượn {model.CurrentLoans}/{model.MaxBooks} sách.";
                    model.CanOverrideBlock = true;
                }
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
        AdminAccount staff, CancellationToken ct = default, string? ipAddress = null, IReadOnlyList<string>? rowIds = null,
        Guid? requestId = null, string? bypassReason = null, bool confirmBypass = false)
    {
        if (string.IsNullOrWhiteSpace(cardCode) || copyCodes.Count == 0)
            return new(false, "Vui lòng nhập mã thẻ và chọn ít nhất một bản sao.");
        if (bypassReason?.Trim().Length > 160)
            return new(false, "Lý do bỏ qua không được vượt quá 160 ký tự.");
        var codes = copyCodes.Select(c => c?.Trim() ?? "").ToArray();
        if (cardCode.Trim().Length > 32 || (rowIds != null && rowIds.Count != codes.Length))
            return new(false, "Mã thẻ hoặc mã vạch không hợp lệ.");
        if (codes.Any(c => c.Length > 50)) return new(false, "Mã vạch không hợp lệ.");
        var hash = PayloadHash(cardCode.Trim(), codes);
        // A serializable transaction includes the existing loan service's SaveChanges.
        // SQL Server update locks serialize requests for the same reader before checking their limit.
        try
        {
            if (requestId.HasValue)
            {
                if (requestId == Guid.Empty) return new(false, "Thiếu định danh lượt mượn.", RequestConflict: true);
                var registered = await RegisterAsync(requestId.Value, hash, cardCode.Trim(), codes, rowIds, staff.Id, ct);
                if (registered != null) return registered;
            }
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            DirectLoanOperation? operation = null;
            if (requestId.HasValue)
            {
                operation = await LockedOperationAsync(db, requestId.Value, ct);
                if (operation == null) return UnknownResult();
                if (operation.StaffId != staff.Id || operation.PayloadHash != hash) return ConflictResult();
                if (operation.CompletedAtUtc != null) return await CompletedResultAsync(db, operation.RequestId, ct);
            }
            var normalizedCard = cardCode.Trim();
            if (db.Database.IsSqlServer())
            {
                await db.ReaderAccounts.FromSqlInterpolated($"SELECT r.* FROM ReaderAccounts r WITH (UPDLOCK, HOLDLOCK) INNER JOIN LibraryCards c ON c.ReaderAccountId = r.Id WHERE c.CardCode = {normalizedCard}")
                    .AsNoTracking().ToListAsync(ct);
            }
            var page = await LookupAsync(normalizedCard, null, ct);
            var rows = await ValidateRowsAsync(codes, rowIds, ct, normalizedCard);
            var policyReason = page.ReaderError;
            if (page.ReaderName != null && page.ReaderError == null && codes.Length > page.RemainingLoans)
            {
                var batchReason = $"Đang mượn {page.CurrentLoans}/{page.MaxBooks} sách, lượt này cần thêm {codes.Length} nhưng chỉ còn hạn mức {page.RemainingLoans} sách.";
                policyReason = batchReason;
            }
            var overrideAllowed = staff.Role is AccountRoles.Librarian or AccountRoles.LibraryManager or AccountRoles.SystemAdmin;
            var batchLimitBlock = page.ReaderName != null && page.ReaderError == null && codes.Length > page.RemainingLoans;
            var hasPolicyBlock = page.ReaderName == null || !string.IsNullOrWhiteSpace(page.ReaderError) || batchLimitBlock;
            var hasBypassReason = !string.IsNullOrWhiteSpace(bypassReason);
            var useOverride = page.ReaderName != null && (page.CanOverrideBlock || batchLimitBlock)
                && overrideAllowed && confirmBypass && hasBypassReason;
            if (hasPolicyBlock && !useOverride)
            {
                var blockedReason = policyReason ?? "Không tìm thấy bạn đọc hoặc thẻ hợp lệ.";
                var reader = await db.LibraryCards.AsNoTracking().Where(c => c.CardCode == normalizedCard)
                    .Select(c => c.ReaderAccount).FirstOrDefaultAsync(ct);
                var target = reader == null
                    ? $"Mã thẻ {normalizedCard} – Lý do: {blockedReason}"
                    : $"Bạn đọc #{reader.Id} {reader.FullName} – Lý do: {blockedReason}";
                db.AuditLogs.Add(AuditLogService.Create(staff.Email, AuditActions.BlockLoan, target, ipAddress));
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                var denial = page.ReaderName == null
                    ? policyReason ?? "Thông tin bạn đọc không hợp lệ."
                    : overrideAllowed && (page.CanOverrideBlock || batchLimitBlock) && (!hasBypassReason || !confirmBypass)
                        ? $"{blockedReason} Muốn bỏ qua chặn, thủ thư hoặc quản lý cần xác nhận và nhập lý do."
                        : blockedReason;
                return new(false, denial, Rows: rows);
            }
            if (rows.Any(r => !r.IsValid))
                return new(false, rows.First(r => !r.IsValid).Error, Rows: rows);
            if (hasPolicyBlock && useOverride)
            {
                var reader = await db.LibraryCards.AsNoTracking().Where(c => c.CardCode == normalizedCard)
                    .Select(c => c.ReaderAccount!).FirstAsync(ct);
                var blockedReason = policyReason!;
                var target = $"Bạn đọc #{reader.Id} {reader.FullName} – Lý do chặn: {blockedReason} – Lý do bỏ qua: {bypassReason!.Trim()}";
                db.AuditLogs.Add(AuditLogService.Create(staff.Email, AuditActions.OverrideBlockLoan, target, ipAddress));
            }
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
                var result = useOverride
                    ? await loans.OverrideCreateForHoldAsync(copy.BookId, card.ReaderAccountId, loanDate,
                        originalDueDate, dueDate, staff.Email, ct)
                    : await loans.CreateForHoldAsync(copy.BookId, card.ReaderAccountId, loanDate,
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
            if (operation != null)
            {
                operation.CompletedAtUtc = DateTime.UtcNow;
                for (var i = 0; i < copies.Count; i++)
                    db.DirectLoanOperationItems.Add(new() { RequestId = operation.RequestId, Position = i,
                        BookLoanId = created.Single(l => l.BookCopyId == copies[i].Id).Id });
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return new(true, Loans: copies.Select(c => created.Single(l => l.BookCopyId == c.Id)).ToList());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Không thể lưu lượt mượn trực tiếp.");
            if (requestId.HasValue)
                return await VerifyAsync(requestId.Value, staff.Id, hash);
            return new(false, "Không thể xác nhận lượt mượn. Vui lòng kiểm tra lại kết quả trước khi thử lại.");
        }
        finally
        {
            // Disposal rolls back an uncommitted transaction; clear tracked inserts/changes so a later save cannot replay them.
            db.ChangeTracker.Clear();
        }
    }
}
