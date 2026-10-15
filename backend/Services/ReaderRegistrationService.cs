using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Data;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed class ReaderRegistrationService(
    ApplicationDbContext dbContext,
    IPasswordHasher<ReaderAccount> passwordHasher,
    ILogger<ReaderRegistrationService> logger,
    IBookLoanService? bookLoanService = null,
    IBookHoldFulfillmentService? holdFulfillmentService = null) : IReaderRegistrationService
{
    // Retained for source compatibility; there is no active-hold count limit.
    public const int MaximumActiveBookHolds = 3;
    public const int PickupDays = 2;
    /// <summary>Số ngày bạn đọc được đôn lên có để nhận sách; hạn là 17:00 ngày mở cửa tương ứng.</summary>
    public static readonly TimeOnly PickupDeadlineTime = new(17, 0);

    // Dùng lại cơ chế dời hạn theo lịch làm việc/ngày nghỉ của nghiệp vụ mượn sách.
    private readonly IBookLoanService loanService =
        bookLoanService ?? new BookLoanService(dbContext, new WorkingScheduleService(dbContext));
    private readonly IBookHoldFulfillmentService holdFulfillmentService =
        holdFulfillmentService ?? new BookHoldFulfillmentService(dbContext,
            bookLoanService ?? new BookLoanService(dbContext, new WorkingScheduleService(dbContext)));

    public async Task<ReaderRegistrationOutcome> RegisterAsync(
        ReaderRegistrationViewModel model,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = model.Email.Trim().ToUpperInvariant();
        var normalizedCode = model.StudentOrStaffCode.Trim().ToUpperInvariant();

        // 1. Kiểm tra email đã tồn tại trước khi tạo tài khoản
        var emailExists = await dbContext.ReaderAccounts
            .AnyAsync(r => r.Email.Trim().ToUpper() == normalizedEmail, cancellationToken);

        // 2. Kiểm tra mã sinh viên hoặc mã cán bộ đã tồn tại trước khi tạo tài khoản
        var codeExists = await dbContext.ReaderAccounts
            .AnyAsync(r => r.StudentOrStaffCode.Trim().ToUpper() == normalizedCode, cancellationToken);

        // 8. Tài khoản bị từ chối do trùng email hoặc trùng mã KHÔNG được tạo thêm bản ghi mới
        if (emailExists || codeExists)
        {
            if (emailExists)
            {
                logger.LogWarning("Đăng ký bị từ chối: Email {Email} đã tồn tại trong hệ thống.", model.Email);
            }
            if (codeExists)
            {
                logger.LogWarning("Đăng ký bị từ chối: Mã {Code} đã tồn tại trong hệ thống.", model.StudentOrStaffCode);
            }

            return ReaderRegistrationOutcome.Duplicate(emailExists, codeExists);
        }

        // 6 & 7. Cho phép đăng ký bình thường với trạng thái "Chờ duyệt"
        var readerAccount = new ReaderAccount
        {
            FullName = model.FullName.Trim(),
            DateOfBirth = model.DateOfBirth!.Value,
            Email = model.Email.Trim(),
            PhoneNumber = model.PhoneNumber.Trim(),
            StudentOrStaffCode = model.StudentOrStaffCode.Trim(),
            Status = "Chờ duyệt",
            CreatedAtUtc = DateTime.UtcNow,
            // Phải xác nhận qua liên kết gửi về email trước khi đăng nhập.
            EmailConfirmed = false,
            RequestedLibraryCardTypeId = model.LibraryCardTypeId
        };

        readerAccount.PasswordHash = passwordHasher.HashPassword(readerAccount, model.Password);

        dbContext.ReaderAccounts.Add(readerAccount);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Đăng ký thành công tài khoản Bạn đọc: {Email}, Mã: {Code}, Trạng thái: Chờ duyệt.",
            readerAccount.Email, readerAccount.StudentOrStaffCode);

        return ReaderRegistrationOutcome.Success(readerAccount);
    }

    public async Task<ReaderAccount?> GetReaderByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await dbContext.ReaderAccounts.Include(reader => reader.LibraryCard)
            .ThenInclude(card => card!.LibraryCardType)
            .SingleOrDefaultAsync(reader => reader.Id == id, cancellationToken);
    }

    public async Task<ReaderContactUpdateResult> UpdateReaderContactAsync(
        int id, string phoneNumber, string address, string email, string currentPassword,
        CancellationToken cancellationToken = default)
    {
        var reader = await dbContext.ReaderAccounts.SingleOrDefaultAsync(account => account.Id == id, cancellationToken);
        if (reader == null) return ReaderContactUpdateResult.NotFound;

        var emailChanged = !string.Equals(reader.Email.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase);
        if (emailChanged && (string.IsNullOrWhiteSpace(currentPassword) ||
            passwordHasher.VerifyHashedPassword(reader, reader.PasswordHash, currentPassword) == PasswordVerificationResult.Failed))
        {
            return ReaderContactUpdateResult.InvalidCurrentPassword;
        }

        if (emailChanged)
        {
            var normalized = email.Trim().ToUpperInvariant();
            var taken = await dbContext.ReaderAccounts
                .AnyAsync(account => account.Id != id && account.Email.ToUpper() == normalized, cancellationToken);
            if (taken) return ReaderContactUpdateResult.EmailInUse;
        }

        reader.PhoneNumber = phoneNumber.Trim();
        reader.Address = address.Trim();
        if (emailChanged)
        {
            // Không đổi ngay: email mới phải được chủ địa chỉ xác nhận, tránh đổi sang email giả hoặc gõ nhầm.
            reader.PendingEmail = email.Trim();
        }
        reader.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return emailChanged ? ReaderContactUpdateResult.EmailChangePending : ReaderContactUpdateResult.Success;
    }

    public async Task<ReaderPasswordChangeResult> ChangeReaderPasswordAsync(
        int id, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            var reader = await dbContext.ReaderAccounts.SingleOrDefaultAsync(
                account => account.Id == id, cancellationToken);
            if (reader == null) return ReaderPasswordChangeResult.NotFound;

            if (passwordHasher.VerifyHashedPassword(reader, reader.PasswordHash, currentPassword) ==
                PasswordVerificationResult.Failed)
            {
                return ReaderPasswordChangeResult.IncorrectCurrentPassword;
            }

            var history = await dbContext.ReaderPasswordHistories
                .Where(entry => entry.ReaderAccountId == id)
                .OrderByDescending(entry => entry.CreatedAtUtc)
                .ThenByDescending(entry => entry.Id)
                .ToListAsync(cancellationToken);

            var passwordWasUsed = passwordHasher.VerifyHashedPassword(reader, reader.PasswordHash, newPassword) !=
                                  PasswordVerificationResult.Failed;
            if (!passwordWasUsed)
            {
                foreach (var previousPassword in history.Take(3))
                {
                    if (passwordHasher.VerifyHashedPassword(reader, previousPassword.PasswordHash, newPassword) !=
                        PasswordVerificationResult.Failed)
                    {
                        passwordWasUsed = true;
                        break;
                    }
                }
            }

            if (passwordWasUsed) return ReaderPasswordChangeResult.PasswordRecentlyUsed;

            var now = DateTime.UtcNow;
            var oldPasswordHash = reader.PasswordHash;
            reader.PasswordHash = passwordHasher.HashPassword(reader, newPassword);
            reader.UpdatedAtUtc = now;
            dbContext.ReaderPasswordHistories.Add(new ReaderPasswordHistory
            {
                ReaderAccountId = reader.Id,
                PasswordHash = oldPasswordHash,
                CreatedAtUtc = now
            });

            if (history.Count > 2)
            {
                dbContext.ReaderPasswordHistories.RemoveRange(history.Skip(2));
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ReaderPasswordChangeResult.Success;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<ReaderAccount?> AuthenticateReaderAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = email.Trim().ToUpperInvariant();
        var reader = await dbContext.ReaderAccounts
            .SingleOrDefaultAsync(r => r.Email.ToUpper() == normalizedEmail, cancellationToken);

        if (reader == null)
        {
            return null;
        }

        var verificationResult = passwordHasher.VerifyHashedPassword(reader, reader.PasswordHash, password);
        return verificationResult != PasswordVerificationResult.Failed ? reader : null;
    }

    public async Task<DocumentHoldOutcome> HoldDocumentAsync(
        int readerAccountId,
        int documentId,
        CancellationToken cancellationToken = default)
    {
        var reader = await dbContext.ReaderAccounts.Include(account => account.LibraryCard)
            .SingleOrDefaultAsync(account => account.Id == readerAccountId, cancellationToken);
        if (reader == null)
        {
            return DocumentHoldOutcome.Failed("Không tìm thấy thông tin tài khoản Bạn đọc.");
        }

        if (!string.Equals(reader.Status, "Đang hoạt động", StringComparison.OrdinalIgnoreCase))
        {
            if (reader.Status.Contains("khóa", StringComparison.OrdinalIgnoreCase))
                return DocumentHoldOutcome.Rejected("Tài khoản của bạn đang bị khóa nên không thể đặt giữ.");
            logger.LogWarning("Từ chối đặt giữ: Tài khoản Bạn đọc {Email} đang ở trạng thái Chờ duyệt.", reader.Email);

            // 6. Hiển thị thông báo rõ ràng rằng tài khoản cần được duyệt trước khi đặt giữ tài liệu
            return DocumentHoldOutcome.Rejected(
                "Tài khoản của bạn đang ở trạng thái Chờ duyệt. Vui lòng xuất trình giấy tờ tại quầy thư viện để được duyệt tài khoản trước khi thực hiện đặt giữ tài liệu.");
        }

        var card = reader.LibraryCard;
        if (card == null || !string.Equals(card.Status, "Đang hoạt động", StringComparison.OrdinalIgnoreCase))
            return DocumentHoldOutcome.Rejected("Thẻ bạn đọc chưa được cấp hoặc không còn hiệu lực.");
        var today = HoldPickupExpirySchedule.GetBusinessDate(DateTime.UtcNow);
        if (card.ExpiresOn < today)
            return DocumentHoldOutcome.Rejected("Thẻ bạn đọc đã hết hạn.");

        var hasOverdueLoan = await dbContext.BookLoans.AnyAsync(loan =>
            loan.ReaderAccountId == readerAccountId && loan.ReturnedAtUtc == null && loan.DueDate < today,
            cancellationToken);
        if (hasOverdueLoan)
            return DocumentHoldOutcome.Rejected("Bạn đọc đang có phiếu mượn quá hạn chưa trả nên không thể đặt giữ sách.");

        var bookExists = await dbContext.Books.AnyAsync(book => book.Id == documentId, cancellationToken);
        if (!bookExists)
        {
            return DocumentHoldOutcome.Failed("Không tìm thấy sách cần đặt giữ.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var hasAvailableCopy = await dbContext.BookCopies.AnyAsync(copy =>
            copy.BookId == documentId && copy.Status == BookCopyStatus.Available, cancellationToken);
        if (!hasAvailableCopy)
        {
            await transaction.RollbackAsync(cancellationToken);
            return DocumentHoldOutcome.Rejected("Tạm dừng đặt sách: hiện kho đã hết bản sao sẵn sàng. Vui lòng thử lại sau.");
        }

        if (await dbContext.BookLoans.AnyAsync(loan =>
                loan.ReaderAccountId == readerAccountId && loan.BookId == documentId && loan.ReturnedAtUtc == null, cancellationToken))
            return DocumentHoldOutcome.Rejected("B\u1ea1n \u0111ang m\u01b0\u1ee3n \u0111\u1ea7u s\u00e1ch n\u00e0y. Vui l\u00f2ng tr\u1ea3 b\u1ea3n \u0111ang m\u01b0\u1ee3n tr\u01b0\u1edbc khi \u0111\u1eb7t gi\u1eef.");

        var createdHold = new BookHold
        {
            ReaderAccountId = readerAccountId,
            BookId = documentId,
            HeldAtUtc = DateTime.UtcNow
        };
        dbContext.BookHolds.Add(createdHold);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return DocumentHoldOutcome.Rejected("Bạn đã đặt giữ cuốn sách này.");
        }

        var fulfillment = await holdFulfillmentService.FulfillNextAsync(documentId, cancellationToken);
        if (fulfillment.ErrorMessage != null)
            logger.LogWarning("Không tính được hạn nhận cho đầu sách #{BookId}: {Reason}", documentId, fulfillment.ErrorMessage);

        int? position = createdHold.Status == BookHoldStatus.Waiting
            ? await dbContext.BookHolds.CountAsync(hold =>
                hold.BookId == documentId && hold.Status == BookHoldStatus.Waiting &&
                (hold.HeldAtUtc < createdHold.HeldAtUtc ||
                 (hold.HeldAtUtc == createdHold.HeldAtUtc && hold.Id < createdHold.Id)), cancellationToken) + 1
            : null;
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Đặt giữ thành công sách #{BookId} cho tài khoản {Email}.", documentId, reader.Email);

        return DocumentHoldOutcome.Success(position, fulfillment);
    }

    public async Task<IReadOnlyList<ReaderBookHoldItem>> GetReaderHoldsAsync(
        int readerAccountId,
        CancellationToken cancellationToken = default) =>
        await dbContext.BookHolds
            .AsNoTracking()
            .Where(hold => hold.ReaderAccountId == readerAccountId)
            .OrderByDescending(hold => hold.HeldAtUtc)
            .ThenByDescending(hold => hold.Id)
            .Select(hold => new ReaderBookHoldItem(
                hold.Id, hold.BookId, hold.Book!.Title, hold.HeldAtUtc, hold.Status,
                // Tính vị trí trên toàn bộ hàng active, gồm cả đơn đã có bản sao chờ nhận.
                BookHoldStatus.ActiveStatuses.Contains(hold.Status)
                    ? dbContext.BookHolds.Count(other =>
                        other.BookId == hold.BookId &&
                        BookHoldStatus.ActiveStatuses.Contains(other.Status) &&
                        (other.HeldAtUtc < hold.HeldAtUtc ||
                         (other.HeldAtUtc == hold.HeldAtUtc && other.Id < hold.Id))) + 1
                    : (int?)null,
                hold.Status == BookHoldStatus.Available ? hold.PickupDeadlineUtc : null,
                hold.BookCopy == null ? null : hold.BookCopy.CopyCode))
            .ToListAsync(cancellationToken);

    public async Task<BookHoldCancelOutcome> CancelReaderHoldAsync(
        int readerAccountId,
        long holdId,
        CancellationToken cancellationToken = default)
    {
        // Lọc theo cả chủ sở hữu: đơn của tài khoản khác được xử lý như không tồn tại.
        var precheck = await CheckCancellableAsync(readerAccountId, holdId, cancellationToken);
        if (precheck != null) return precheck;

        // Tính hạn nhận trước khi mở giao dịch: việc đọc lịch làm việc có thể tự khởi tạo dữ liệu lịch.
        // Hủy đơn + đôn hàng đợi + cập nhật bản sao trong cùng một giao dịch.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        precheck = await CheckCancellableAsync(readerAccountId, holdId, cancellationToken);
        if (precheck != null) return precheck;

        var hold = await dbContext.BookHolds.SingleAsync(h => h.Id == holdId, cancellationToken);
        var firstActiveHoldId = await dbContext.BookHolds
            .Where(h => h.BookId == hold.BookId && BookHoldStatus.ActiveStatuses.Contains(h.Status))
            .OrderBy(h => h.HeldAtUtc).ThenBy(h => h.Id)
            .Select(h => h.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var wasFirstInQueue = firstActiveHoldId == hold.Id;

        hold.Status = BookHoldStatus.Cancelled;
        hold.CancelledAtUtc = DateTime.UtcNow;
        hold.CancellationReason = "Bạn đọc tự hủy đơn đặt giữ.";
        var copy = hold.BookCopyId is { } copyId
            ? await dbContext.BookCopies.SingleOrDefaultAsync(c => c.Id == copyId && c.BookId == hold.BookId, cancellationToken)
            : null;
        hold.BookCopyId = null;

        hold.PickupDeadlineUtc = null;
        if (copy is not null)
        {
            copy.Status = BookCopyStatus.Available;
            copy.StatusReason = null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        BookHoldFulfillmentResult? fulfillment = null;
        if (wasFirstInQueue || copy is not null)
            fulfillment = await holdFulfillmentService.FulfillNextAsync(hold.BookId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        long? promotedHoldId = fulfillment is { IsAssigned: true } ? fulfillment.HoldId : null;
        long? releasedCopyId = copy is not null && fulfillment?.IsAssigned != true ? copy.Id : null;

        logger.LogInformation(
            "Bạn đọc #{ReaderId} đã hủy đơn đặt giữ #{HoldId}; đôn đơn {PromotedHoldId}; trả bản sao {ReleasedCopyId}.",
            readerAccountId, holdId, promotedHoldId, releasedCopyId);
        return BookHoldCancelOutcome.Success(promotedHoldId, releasedCopyId);
    }

    private async Task<BookHoldCancelOutcome?> CheckCancellableAsync(
        int readerAccountId, long holdId, CancellationToken cancellationToken)
    {
        var status = await dbContext.BookHolds.AsNoTracking()
            .Where(h => h.Id == holdId && h.ReaderAccountId == readerAccountId)
            .Select(h => h.Status)
            .SingleOrDefaultAsync(cancellationToken);
        if (status == null) return BookHoldCancelOutcome.NotFound();
        return status == BookHoldStatus.Waiting || BookHoldStatus.WaitingPickupStatuses.Contains(status)
            ? null : BookHoldCancelOutcome.NotWaiting(status);
    }

    public Task<IReadOnlyList<ReaderAccount>> GetPendingReadersAsync(CancellationToken cancellationToken = default) =>
        GetPendingReadersAsync(null, null, null, cancellationToken);

    public async Task<IReadOnlyList<ReaderAccount>> GetPendingReadersAsync(
        string? search,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.ReaderAccounts.Where(reader => reader.Status == "Chờ duyệt");

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var normalizedTerm = term.ToUpper();
            query = query.Where(reader =>
                reader.FullName.ToUpper().Contains(normalizedTerm) ||
                reader.StudentOrStaffCode.ToUpper().Contains(normalizedTerm));
        }

        if (fromDate.HasValue)
        {
            var fromUtc = DateTime.SpecifyKind(fromDate.Value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            query = query.Where(reader => reader.CreatedAtUtc >= fromUtc);
        }

        if (toDate.HasValue)
        {
            var untilUtc = DateTime.SpecifyKind(toDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            query = query.Where(reader => reader.CreatedAtUtc < untilUtc);
        }

        return await query.OrderBy(reader => reader.CreatedAtUtc).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LibraryCardType>> GetActiveCardTypesAsync(CancellationToken cancellationToken = default) =>
        await dbContext.LibraryCardTypes.Where(type => type.IsActive).OrderBy(type => type.Name)
            .ToListAsync(cancellationToken);

    public async Task<ReaderApprovalOutcome> ApproveReaderAsync(
        ApproveReaderViewModel model, CancellationToken cancellationToken = default)
    {
        if (model.ReaderAccountId <= 0 || model.LibraryCardTypeId <= 0)
            return ReaderApprovalOutcome.Failed("Thông tin duyệt hồ sơ không hợp lệ.");
        if (model.ExpiresOn < model.IssuedOn)
            return ReaderApprovalOutcome.Failed("Ngày hết hạn không được nhỏ hơn ngày cấp.");

        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var reader = await dbContext.ReaderAccounts.SingleOrDefaultAsync(
                account => account.Id == model.ReaderAccountId, cancellationToken);
            if (reader == null)
                return ReaderApprovalOutcome.Failed("Không tìm thấy hồ sơ bạn đọc.");
            if (!string.Equals(reader.Status, "Chờ duyệt", StringComparison.OrdinalIgnoreCase))
                return ReaderApprovalOutcome.Failed("Hồ sơ này không còn ở trạng thái Chờ duyệt.");

            var cardType = await dbContext.LibraryCardTypes.SingleOrDefaultAsync(
                type => type.Id == model.LibraryCardTypeId && type.IsActive, cancellationToken);
            if (cardType == null)
                return ReaderApprovalOutcome.Failed("Loại thẻ không tồn tại hoặc đã ngừng hoạt động.");
            if (await dbContext.LibraryCards.AnyAsync(card => card.ReaderAccountId == reader.Id, cancellationToken))
                return ReaderApprovalOutcome.Failed("Hồ sơ này đã được cấp thẻ.");

            LibraryCard? card = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var code = $"LIB{reader.Id:D6}" + (attempt == 0 ? string.Empty : $"{attempt + 1:D2}");
                if (await dbContext.LibraryCards.AnyAsync(item => item.CardCode == code, cancellationToken) ||
                    await dbContext.BookCopies.AnyAsync(item => item.CopyCode == code, cancellationToken))
                    continue;

                card = new LibraryCard
                {
                    CardCode = code,
                    ReaderAccountId = reader.Id,
                    LibraryCardTypeId = cardType.Id,
                    IssuedOn = model.IssuedOn,
                    ExpiresOn = model.ExpiresOn,
                    Status = "Đang hoạt động"
                };
                break;
            }
            if (card == null)
                return ReaderApprovalOutcome.Failed("Không thể sinh mã thẻ duy nhất. Vui lòng thử lại.");

            dbContext.LibraryCards.Add(card);
            reader.Status = "Đang hoạt động";
            reader.UpdatedAtUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ReaderApprovalOutcome.Success(card);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<ReaderRejectionOutcome> RejectReaderAsync(
        int readerAccountId,
        string? rejectionReason,
        CancellationToken cancellationToken = default)
    {
        var normalizedReason = rejectionReason?.Trim();
        if (readerAccountId <= 0 || string.IsNullOrWhiteSpace(normalizedReason))
            return ReaderRejectionOutcome.Failed("Vui lòng nhập lý do từ chối.");
        if (normalizedReason.Length > 1000)
            return ReaderRejectionOutcome.Failed("Lý do từ chối không được vượt quá 1000 ký tự.");

        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var reader = await dbContext.ReaderAccounts.SingleOrDefaultAsync(
                account => account.Id == readerAccountId, cancellationToken);
            if (reader == null)
                return ReaderRejectionOutcome.Failed("Không tìm thấy hồ sơ bạn đọc.");
            if (!string.Equals(reader.Status, "Chờ duyệt", StringComparison.OrdinalIgnoreCase))
                return ReaderRejectionOutcome.Failed("Chỉ có thể từ chối hồ sơ đang ở trạng thái Chờ duyệt.");

            reader.Status = "Từ chối";
            reader.RejectionReason = normalizedReason;
            reader.UpdatedAtUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ReaderRejectionOutcome.Success();
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
