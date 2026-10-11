using System.Data;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed class BookLoanService(ApplicationDbContext db, IWorkingScheduleService workingScheduleService) : IBookLoanService
{
    public const int DefaultLoanDays = 14;
    public const int DefaultRenewalDays = 7;

    public async Task<IReadOnlyList<BookLoan>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await db.BookLoans.Include(loan => loan.Book)
            .Include(loan => loan.ReaderAccount).ThenInclude(reader => reader!.LibraryCard)
                .ThenInclude(card => card!.LibraryCardType)
            .OrderByDescending(loan => loan.CreatedAtUtc).ToListAsync(cancellationToken);

    public async Task<ReaderBorrowingStatus?> GetBorrowingStatusAsync(
        int readerAccountId, CancellationToken cancellationToken = default)
    {
        var reader = await db.ReaderAccounts.AsNoTracking()
            .Include(item => item.LibraryCard)
            .ThenInclude(card => card!.LibraryCardType)
            .FirstOrDefaultAsync(item => item.Id == readerAccountId, cancellationToken);
        if (reader == null) return null;
        var cardType = reader.LibraryCard?.LibraryCardType;
        var borrowingLimit = cardType?.MaxBorrowedBooks ?? LibraryCardType.DefaultMaxBorrowedBooks;
        return await GetBorrowingStatusAsync(reader, borrowingLimit, cancellationToken);
    }

    public async Task<BookLoanOutcome> CreateAsync(
        int bookId, int readerAccountId, DateOnly loanDate, CancellationToken cancellationToken = default,
        string? actor = null, string? ipAddress = null, string? overrideReason = null,
        Guid? overrideRequestId = null, string? actorRole = null)
    {
        var hasOverrideData = overrideReason is not null || overrideRequestId.HasValue;
        if (hasOverrideData)
        {
            if (!string.Equals(actorRole, AccountRoles.LibraryManager, StringComparison.Ordinal))
                return new(false, "Chỉ quản lý thư viện được phép bỏ qua chặn.",
                    ReasonCode: "LOAN_OVERRIDE_FORBIDDEN");
            if (string.IsNullOrWhiteSpace(overrideReason) || overrideReason.Trim().Length > 1000 ||
                overrideRequestId is null)
                return new(false, "Vui lòng nhập lý do bỏ qua hợp lệ và gửi mã yêu cầu.",
                    ReasonCode: "INVALID_LOAN_OVERRIDE");
            overrideReason = overrideReason.Trim();
        }

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

        if (overrideRequestId is { } requestId &&
            await db.AuditBlockLogs.AnyAsync(item => item.OverrideRequestId == requestId, cancellationToken))
            return new(false, "Yêu cầu bỏ qua này đã được xử lý; không thể sử dụng lại.",
                ReasonCode: "LOAN_OVERRIDE_ALREADY_PROCESSED");

        IQueryable<ReaderAccount> readerQuery = db.ReaderAccounts;
        if (db.Database.IsSqlServer())
        {
            readerQuery = db.ReaderAccounts.FromSqlInterpolated(
                $"SELECT * FROM [ReaderAccounts] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {readerAccountId}");
        }
        var reader = await readerQuery
            .Include(item => item.LibraryCard)
            .ThenInclude(card => card!.LibraryCardType)
            .FirstOrDefaultAsync(item => item.Id == readerAccountId, cancellationToken);
        if (reader == null)
        {
            if (hasOverrideData)
            {
                await SaveUnknownReaderOverrideFailureAsync(
                    readerAccountId, overrideRequestId!.Value, overrideReason!, actorRole!,
                    actor, ipAddress, "Không tìm thấy bạn đọc.", cancellationToken);
                if (transaction != null) await transaction.CommitAsync(cancellationToken);
            }
            return new(false, "Không tìm thấy bạn đọc.");
        }
        var cardType = reader.LibraryCard?.LibraryCardType;
        var borrowingLimit = cardType?.MaxBorrowedBooks ?? LibraryCardType.DefaultMaxBorrowedBooks;
        var status = await GetBorrowingStatusAsync(reader, borrowingLimit, cancellationToken);
        if (!string.Equals(reader.Status, "Đang hoạt động", StringComparison.OrdinalIgnoreCase))
        {
            if (hasOverrideData)
            {
                await SaveOverrideLogAsync(
                    reader, status, overrideRequestId!.Value, overrideReason!, actorRole!,
                    actor, ipAddress, "Failed", null, "Bạn đọc chưa ở trạng thái hoạt động.", cancellationToken);
                if (transaction != null) await transaction.CommitAsync(cancellationToken);
            }
            return new(false, "Bạn đọc chưa ở trạng thái hoạt động.");
        }

        var book = await db.Books.FindAsync([bookId], cancellationToken);
        if (book == null)
        {
            if (hasOverrideData)
            {
                await SaveOverrideLogAsync(
                    reader, status, overrideRequestId!.Value, overrideReason!, actorRole!,
                    actor, ipAddress, "Failed", null, "Không tìm thấy sách.", cancellationToken);
                if (transaction != null) await transaction.CommitAsync(cancellationToken);
            }
            return new(false, "Không tìm thấy sách.");
        }
        if (status.IsBlocked && !hasOverrideData)
        {
            var reasons = status.BlockingReasons.Select(reason => reason.Message).ToArray();
            var auditAction = status.BlockingReasons.Count == 1 &&
                status.BlockingReasons[0].Code == "BORROWING_LIMIT_REACHED"
                ? AuditActions.BlockLoanLimit
                : AuditActions.BlockLoanPolicy;

            // Tạo bản ghi AuditBlockLog riêng để thuận tiện tra cứu các lần bị chặn
            db.AuditBlockLogs.Add(new AuditBlockLog
            {
                OccurredAtUtc = DateTime.UtcNow,
                ReaderAccountId = reader.Id,
                ReaderCode = reader.StudentOrStaffCode,
                ReaderName = reader.FullName,
                BorrowedCount = status.BorrowedCount,
                BorrowingLimit = status.BorrowingLimit,
                OverdueLoanCount = status.OverdueLoanCount,
                OutstandingBalance = status.OutstandingBalance,
                CardStatus = status.CardStatus,
                CardExpiresOn = status.CardExpiresOn,
                Reasons = string.Join("; ", reasons),
                BlockingReasonCodes = string.Join("|", status.BlockingReasons.Select(reason => reason.Code)),
                EventType = LoanBlockHistoryEventTypes.FromBlockingCodes(
                    status.BlockingReasons.Select(reason => reason.Code)),
                Actor = actor ?? "Không xác định",
                ActorRole = actorRole,
                IpAddress = ipAddress
            });

            // Vẫn ghi AuditLog tổng quát để duy trì lịch sử hệ thống hiện có
            db.AuditLogs.Add(AuditLogService.Create(
                actor ?? "Không xác định",
                auditAction,
                $"Bạn đọc {reader.StudentOrStaffCode} - {reader.FullName} (ID:{reader.Id}); " +
                $"{status.BorrowedCount}/{status.BorrowingLimit}; thẻ={status.CardStatus ?? "chưa cấp"}; " +
                $"hết hạn={status.CardExpiresOn?.ToString("yyyy-MM-dd") ?? "không xác định"}; " +
                $"quá hạn chưa trả={status.OverdueLoanCount}; nợ chưa thanh toán={ReaderBorrowingStatus.FormatVnd(status.OutstandingBalance)} VND; " +
                $"kết quả=bị chặn; lý do: {string.Join("; ", reasons)}",
                ipAddress));

            await db.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
            var message = reasons.Length == 1 &&
                status.BlockingReasons[0].Code == "BORROWING_LIMIT_REACHED"
                    ? $"Đang mượn {status.BorrowedCount}/{status.BorrowingLimit} cuốn, không thể mượn thêm."
                    : reasons.Length == 1 && status.BlockingReasons[0].Code == "UNPAID_FEE"
                        ? reasons[0]
                        : $"Không thể lập phiếu mượn vì các lý do sau:\n{string.Join("\n", reasons.Select((reason, index) => $"{index + 1}. {reason}"))}";
            return new(false, message,
                BorrowedCount: status.BorrowedCount,
                BorrowingLimit: status.BorrowingLimit,
                ReasonCode: reasons.Length == 1 ? status.BlockingReasons[0].Code : "BORROWING_POLICY_VIOLATION",
                BlockingReasons: reasons,
                OutstandingBalance: status.OutstandingBalance);
        }

        var originalDueDate = loanDate.AddDays(DefaultLoanDays);
        DateOnly dueDate;
        try { dueDate = await AdjustDueDateAsync(originalDueDate, cancellationToken); }
        catch (InvalidOperationException exception)
        {
            if (hasOverrideData)
            {
                await SaveOverrideLogAsync(
                    reader, status, overrideRequestId!.Value, overrideReason!, actorRole!,
                    actor, ipAddress, "Failed", null, exception.Message, cancellationToken);
                if (transaction != null) await transaction.CommitAsync(cancellationToken);
            }
            return new(false, exception.Message);
        }

        var loan = new BookLoan
        {
            BookId = bookId,
            ReaderAccountId = readerAccountId,
            LoanDate = loanDate,
            OriginalDueDate = originalDueDate,
            DueDate = dueDate,
            CreatedAtUtc = DateTime.UtcNow,
            Book = book,
            ReaderAccount = reader
        };
        db.BookLoans.Add(loan);
        await db.SaveChangesAsync(cancellationToken);
        if (hasOverrideData)
        {
            await SaveOverrideLogAsync(
                reader, status, overrideRequestId!.Value, overrideReason!, actorRole!,
                actor, ipAddress, status.IsBlocked ? "Used" : "NotRequired",
                loan.Id, null, cancellationToken);
        }
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return new(true, Loan: loan);
    }

    private async Task SaveOverrideLogAsync(
        ReaderAccount reader, ReaderBorrowingStatus status, Guid requestId,
        string reason, string actorRole, string? actor, string? ipAddress,
        string outcome, long? loanId, string? failure, CancellationToken cancellationToken)
    {
        var blockedReasons = status.BlockingReasons.Select(item => item.Message).ToArray();
        var outcomeText = outcome switch
        {
            "Used" => "đã tạo phiếu mượn bằng ngoại lệ",
            "NotRequired" => "đã tạo phiếu mượn; tại thời điểm xác nhận không còn điều kiện chặn",
            _ => $"thất bại: {failure}"
        };
        var auditTarget =
            $"Người thực hiện: {actor ?? "Không xác định"} ({AccountRoles.DisplayName(actorRole)}); " +
            $"bạn đọc: {reader.StudentOrStaffCode} - {reader.FullName} (ID:{reader.Id}); " +
            $"lý do chặn: {(blockedReasons.Length == 0 ? "không còn vi phạm tại thời điểm xác nhận" : string.Join("; ", blockedReasons))}; " +
            $"lý do bỏ qua: {reason}; kết quả: {outcomeText}; phiếu mượn ID: {loanId?.ToString() ?? "không có"}";

        db.AuditBlockLogs.Add(new AuditBlockLog
        {
            OccurredAtUtc = DateTime.UtcNow,
            ReaderAccountId = reader.Id,
            ReaderCode = reader.StudentOrStaffCode,
            ReaderName = reader.FullName,
            BorrowedCount = status.BorrowedCount,
            BorrowingLimit = status.BorrowingLimit,
            OverdueLoanCount = status.OverdueLoanCount,
            OutstandingBalance = status.OutstandingBalance,
            CardStatus = status.CardStatus,
            CardExpiresOn = status.CardExpiresOn,
            Reasons = string.Join("; ", blockedReasons),
            Actor = actor ?? "Không xác định",
            ActorRole = actorRole,
            IpAddress = ipAddress,
            OverrideRequestId = requestId,
            OverrideReason = reason,
            Outcome = outcome,
            BlockingReasonCodes = string.Join("|", status.BlockingReasons.Select(item => item.Code)),
            EventType = outcome switch
            {
                "Used" => LoanBlockHistoryEventTypes.OverrideUsed,
                "NotRequired" => LoanBlockHistoryEventTypes.OverrideNotRequired,
                _ => LoanBlockHistoryEventTypes.OverrideFailed
            },
            FailureReason = failure,
            BookLoanId = loanId
        });
        db.AuditLogs.Add(AuditLogService.Create(
            actor ?? "Không xác định", AuditActions.OverrideLoanPolicy, auditTarget, ipAddress));
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SaveUnknownReaderOverrideFailureAsync(
        int readerAccountId, Guid requestId, string reason, string actorRole,
        string? actor, string? ipAddress, string failure, CancellationToken cancellationToken)
    {
        var actorName = actor ?? "Không xác định";
        db.AuditBlockLogs.Add(new AuditBlockLog
        {
            OccurredAtUtc = DateTime.UtcNow,
            ReaderAccountId = readerAccountId,
            ReaderCode = "Không xác định",
            ReaderName = "Không xác định",
            BorrowedCount = 0,
            BorrowingLimit = 0,
            OverdueLoanCount = 0,
            OutstandingBalance = 0m,
            Reasons = "Không thể xác định điều kiện chặn vì không tìm thấy bạn đọc.",
            Actor = actorName,
            ActorRole = actorRole,
            IpAddress = ipAddress,
            OverrideRequestId = requestId,
            OverrideReason = reason,
            Outcome = "Failed",
            EventType = LoanBlockHistoryEventTypes.OverrideFailed,
            FailureReason = failure
        });
        db.AuditLogs.Add(AuditLogService.Create(
            actorName, AuditActions.OverrideLoanPolicy,
            $"Người thực hiện: {actorName} ({AccountRoles.DisplayName(actorRole)}); " +
            $"bạn đọc ID:{readerAccountId} không xác định; lý do bỏ qua: {reason}; kết quả: thất bại: {failure}",
            ipAddress));
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ReaderBorrowingStatus> GetBorrowingStatusAsync(
        ReaderAccount reader, int borrowingLimit, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var card = reader.LibraryCard;
        var borrowedCount = await db.BookLoans.CountAsync(
            loan => loan.ReaderAccountId == reader.Id && loan.ReturnedAtUtc == null,
            cancellationToken);
        var overdueLoanCount = await db.BookLoans.CountAsync(
            loan => loan.ReaderAccountId == reader.Id &&
                loan.ReturnedAtUtc == null && loan.DueDate < today,
            cancellationToken);
        var outstandingBalance = Math.Max(0m, reader.OutstandingBalance);

        var violations = new List<BorrowingPolicyViolation>();
        if (card?.ExpiresOn < today)
            violations.Add(new("CARD_EXPIRED", "Thẻ thư viện đã hết hạn."));
        if (string.Equals(card?.Status?.Trim(), "Bị khóa", StringComparison.OrdinalIgnoreCase))
            violations.Add(new("CARD_LOCKED", "Thẻ thư viện đang bị khóa."));
        if (overdueLoanCount > 0)
            violations.Add(new("OVERDUE_UNRETURNED_LOANS", "Bạn đọc có sách quá hạn chưa trả."));
        if (outstandingBalance > 0m)
            violations.Add(new("UNPAID_FEE",
                $"Còn nợ {ReaderBorrowingStatus.FormatVnd(outstandingBalance)} VND, không thể mượn sách."));
        if (borrowedCount >= borrowingLimit)
            violations.Add(new("BORROWING_LIMIT_REACHED",
                $"Đang mượn {borrowedCount}/{borrowingLimit} cuốn, không thể mượn thêm."));

        return new ReaderBorrowingStatus(
            reader.Id, reader.StudentOrStaffCode, reader.FullName,
            card?.LibraryCardType?.Name ?? "Chưa xác định loại thẻ",
            borrowedCount, borrowingLimit, card?.Status, card?.ExpiresOn,
            overdueLoanCount, outstandingBalance, violations);
    }

    public async Task<RenewBookLoanOutcome> RenewAsync(long loanId, DateOnly today, CancellationToken cancellationToken = default)
    {
        var loan = await db.BookLoans.Include(item => item.ReaderAccount)
            .ThenInclude(reader => reader!.LibraryCard)
            .ThenInclude(card => card!.LibraryCardType)
            .FirstOrDefaultAsync(item => item.Id == loanId, cancellationToken);
        if (loan == null) return new(false, "Không tìm thấy phiếu mượn.");
        if (loan.ReturnedAtUtc is not null) return new(false, "Phiếu mượn đã được ghi nhận trả sách.");
        if (loan.DueDate < today) return new(false, "Phiếu mượn đã quá hạn.");
        var cardType = loan.ReaderAccount?.LibraryCard?.LibraryCardType;
        if (cardType == null) return new(false, "Bạn đọc chưa có loại thẻ hợp lệ.");
        if (loan.RenewalCount >= cardType.MaxRenewals)
            return new(false, "Bạn đã sử dụng hết số lần gia hạn cho phép của loại thẻ.");

        var hasOtherOverdueLoan = await db.BookLoans.AnyAsync(other =>
            other.ReaderAccountId == loan.ReaderAccountId && other.Id != loan.Id &&
            other.ReturnedAtUtc == null && other.DueDate < today,
            cancellationToken);
        var hasOutstandingBalance = loan.ReaderAccount!.OutstandingBalance > 0;
        if (hasOtherOverdueLoan && hasOutstandingBalance)
            return new(false,
                "Không thể gia hạn vì bạn đọc đang có phiếu mượn khác quá hạn và còn phí/phạt chưa thanh toán.",
                ReasonCode: "OTHER_OVERDUE_LOAN_AND_UNPAID_FEE");
        if (hasOtherOverdueLoan)
            return new(false,
                "Không thể gia hạn vì bạn đọc đang có phiếu mượn khác quá hạn.",
                ReasonCode: "OTHER_OVERDUE_LOAN");
        if (hasOutstandingBalance)
            return new(false,
                "Không thể gia hạn vì tài khoản bạn đọc đang còn phí/phạt chưa thanh toán.",
                ReasonCode: "UNPAID_FEE");

        var oldDueDate = loan.DueDate;
        DateOnly newDueDate;
        try { newDueDate = await AdjustDueDateAsync(oldDueDate.AddDays(DefaultRenewalDays), cancellationToken); }
        catch (InvalidOperationException exception) { return new(false, exception.Message); }

        loan.DueDate = newDueDate;
        loan.RenewalCount++;
        await db.SaveChangesAsync(cancellationToken);
        return new(true, Loan: loan, OldDueDate: oldDueDate);
    }

    public async Task<ReturnBookLoanOutcome> ReturnAsync(
        long loanId, string? actor, string? ipAddress, CancellationToken cancellationToken = default)
    {
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

        IQueryable<BookLoan> loanQuery = db.BookLoans;
        if (db.Database.IsSqlServer())
        {
            loanQuery = db.BookLoans.FromSqlInterpolated(
                $"SELECT * FROM [BookLoans] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {loanId}");
        }
        var loan = await loanQuery.Include(item => item.ReaderAccount)
            .FirstOrDefaultAsync(item => item.Id == loanId, cancellationToken);
        if (loan == null) return new(false, "Không tìm thấy phiếu mượn.");
        if (loan.ReturnedAtUtc is not null) return new(false, "Phiếu mượn đã được ghi nhận trả sách.");

        loan.ReturnedAtUtc = DateTime.UtcNow;
        db.AuditLogs.Add(AuditLogService.Create(
            actor ?? "Không xác định",
            AuditActions.ReturnBookLoan,
            $"Phiếu mượn {loan.Id}; bạn đọc {loan.ReaderAccount?.StudentOrStaffCode} - " +
            $"{loan.ReaderAccount?.FullName} (ID: {loan.ReaderAccountId}); sách ID: {loan.BookId}",
            ipAddress));
        await db.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return new(true, Loan: loan);
    }

    public async Task<DateOnly> AdjustDueDateAsync(DateOnly proposedDate, CancellationToken cancellationToken = default)
    {
        var weeklySchedules = await workingScheduleService.GetWeeklySchedulesAsync(cancellationToken);
        var holidayDates = (await workingScheduleService.GetHolidayClosuresAsync(cancellationToken))
            .Select(holiday => holiday.HolidayDate).ToHashSet();
        var openByDay = weeklySchedules.ToDictionary(schedule => schedule.DayOfWeek, schedule => schedule.IsOpen);

        return DueDateAdjuster.AdjustDueDate(proposedDate,
            date => !holidayDates.Contains(date) && openByDay.TryGetValue(date.DayOfWeek, out var isOpen) && isOpen);
    }
}
