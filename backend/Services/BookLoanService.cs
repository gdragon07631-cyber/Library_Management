using Microsoft.EntityFrameworkCore;
using System.Data;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed class BookLoanService(ApplicationDbContext db, IWorkingScheduleService workingScheduleService) : IBookLoanService
{
    public const int DefaultLoanDays = 14;
    public const int DefaultRenewalDays = 7;

    public async Task<IReadOnlyList<BookLoan>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await db.BookLoans.Include(loan => loan.Book)
            .Include(loan => loan.BookCopy)
            .Include(loan => loan.ReaderAccount).ThenInclude(reader => reader!.LibraryCard)
                .ThenInclude(card => card!.LibraryCardType)
            .OrderByDescending(loan => loan.CreatedAtUtc).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<OverdueLoanItem>> GetOverdueAsync(DateOnly today, CancellationToken cancellationToken = default)
        => await GetOverdueAsync(today, OverdueLoanRange.All, cancellationToken);

    public async Task<IReadOnlyList<OverdueLoanItem>> GetOverdueAsync(
        DateOnly today, OverdueLoanRange range, CancellationToken cancellationToken = default)
    {
        var candidates = await db.BookLoans
            .Include(loan => loan.Book)
            .Include(loan => loan.BookCopy)
            .Include(loan => loan.ReaderAccount).ThenInclude(reader => reader!.LibraryCard)
            .Include(loan => loan.ContactHistories)
            .Where(loan => loan.DueDate < today)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0) return [];

        var firstCountedDate = candidates.Min(loan => loan.DueDate).AddDays(1);
        var openDates = await workingScheduleService.GetOpenDatesAsync(firstCountedDate, today, cancellationToken);

        return candidates
            .Where(loan => !loan.IsReturned)
            .Select(loan => new OverdueLoanItem(
                loan.Id,
                openDates.Count(date => date > loan.DueDate),
                loan.ReaderAccount?.FullName ?? string.Empty,
                loan.ReaderAccount?.PhoneNumber ?? string.Empty,
                loan.ReaderAccount?.LibraryCard?.CardCode,
                loan.Book?.Title ?? string.Empty,
                loan.LoanDate,
                loan.DueDate,
                loan.BookCopy?.CopyCode,
                loan.ContactHistories.OrderByDescending(history => history.CreatedAtUtc).ThenByDescending(history => history.Id)
                    .Select(history => new LoanContactHistoryItem(history.Id, history.BookLoanId, history.CreatedAtUtc,
                        history.Note, history.ContactedByAdminAccountId, history.ContactedBy)).FirstOrDefault()))
            .Where(item => item.DaysOverdue > 0)
            .Where(item => range switch
            {
                OverdueLoanRange.OneToSevenDays => item.DaysOverdue <= 7,
                OverdueLoanRange.MoreThanSevenDays => item.DaysOverdue > 7,
                OverdueLoanRange.MoreThanThirtyDays => item.DaysOverdue > 30,
                _ => true
            })
            .OrderByDescending(item => item.DaysOverdue)
            .ThenBy(item => item.DueDate)
            .ThenBy(item => item.LoanId)
            .ToList();
    }

    public Task<BookLoanOutcome> CreateAsync(int bookId, int readerAccountId, DateOnly loanDate, CancellationToken cancellationToken = default) =>
        CreateAsync(bookId, readerAccountId, loanDate, null, cancellationToken);

    public async Task<BookLoanOutcome> CreateAsync(int bookId, int readerAccountId, DateOnly loanDate, string? actor, CancellationToken cancellationToken = default)
    {
        var result = await CreateManyAsync([bookId], readerAccountId, loanDate, actor, cancellationToken);
        if (!result.IsSuccess)
        {
            return new(false, result.ErrorMessage);
        }
        return new(true, Loan: result.Loans.FirstOrDefault());
    }

    public Task<BatchBookLoanOutcome> CreateManyAsync(
        IReadOnlyList<int> bookIds, int readerAccountId, DateOnly loanDate, CancellationToken cancellationToken = default) =>
        CreateManyAsync(bookIds, readerAccountId, loanDate, null, cancellationToken);

    public async Task<BatchBookLoanOutcome> CreateManyAsync(
        IReadOnlyList<int> bookIds, int readerAccountId, DateOnly loanDate, string? actor, CancellationToken cancellationToken = default) =>
        await CreateManyCoreAsync(bookIds, readerAccountId, loanDate, actor, null, null, cancellationToken);

    public async Task<BookLoanOutcome> CreateForHoldAsync(
        int bookId, int readerAccountId, DateOnly loanDate, DateOnly originalDueDate, DateOnly dueDate,
        string? actor, CancellationToken cancellationToken = default)
    {
        var result = await CreateManyCoreAsync([bookId], readerAccountId, loanDate, actor,
            originalDueDate, dueDate, cancellationToken);
        return result.IsSuccess
            ? new(true, Loan: result.Loans.FirstOrDefault())
            : new(false, result.ErrorMessage);
    }

    public async Task<BookLoanOutcome> OverrideCreateForHoldAsync(
        int bookId, int readerAccountId, DateOnly loanDate, DateOnly originalDueDate, DateOnly dueDate,
        string actor, CancellationToken cancellationToken = default)
    {
        var result = await CreateManyCoreAsync([bookId], readerAccountId, loanDate, actor,
            originalDueDate, dueDate, cancellationToken, bypassEligibility: true);
        return result.IsSuccess
            ? new(true, Loan: result.Loans.FirstOrDefault())
            : new(false, result.ErrorMessage);
    }

    private async Task<BatchBookLoanOutcome> CreateManyCoreAsync(
        IReadOnlyList<int> bookIds, int readerAccountId, DateOnly loanDate, string? actor,
        DateOnly? explicitOriginalDueDate, DateOnly? explicitDueDate, CancellationToken cancellationToken, bool bypassEligibility = false)
    {
        if (bookIds == null || bookIds.Count == 0)
            return new(false, "Vui lòng chọn ít nhất một cuốn sách.", []);

        if (bookIds.Distinct().Count() != bookIds.Count)
            return new(false, "Danh sách sách có mã bị trùng.", []);

        var reader = await db.ReaderAccounts
            .Include(r => r.LibraryCard)
                .ThenInclude(card => card!.LibraryCardType)
            .FirstOrDefaultAsync(r => r.Id == readerAccountId, cancellationToken);
        if (reader == null) return new(false, "Không tìm thấy bạn đọc.", []);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var checkDate = loanDate != default ? loanDate : today;

        var isCardLocked = reader.IsLocked
            || (reader.LibraryCard != null && string.Equals(reader.LibraryCard.Status, "Bị khoá", StringComparison.OrdinalIgnoreCase))
            || (reader.LibraryCard != null && reader.LibraryCard.IsLocked)
            || reader.Status.Contains("khóa", StringComparison.OrdinalIgnoreCase)
            || reader.Status.Contains("khoá", StringComparison.OrdinalIgnoreCase);

        var isCardExpired = reader.LibraryCard != null && reader.LibraryCard.ExpiresOn < checkDate;

        var readerLoans = await db.BookLoans
            .Where(l => l.ReaderAccountId == readerAccountId)
            .ToListAsync(cancellationToken);

        var hasOverdueLoan = readerLoans.Any(l => !l.IsReturned && l.DueDate < checkDate);

        var totalDebt = reader.TotalDebt;
        var hasUnpaidFee = totalDebt > 0m;

        var maxBooks = reader.LibraryCard?.LibraryCardType?.MaxBooks ?? LibraryCardType.DefaultMaxBooks;
        var currentLoans = readerLoans.Count(l => !l.IsReturned);

        var isLimitReached = currentLoans >= maxBooks;
        var isBatchExceeded = !isLimitReached && (currentLoans + bookIds.Count > maxBooks);

        var errors = new List<string>();

        if (isCardLocked)
        {
            errors.Add("Thẻ bạn đọc đang bị khoá.");
        }

        if (isCardExpired)
        {
            errors.Add("Thẻ bạn đọc đã hết hạn.");
        }

        if (hasOverdueLoan)
        {
            errors.Add("Bạn đọc đang có phiếu mượn quá hạn chưa trả.");
        }

        if (hasUnpaidFee)
        {
            errors.Add($"Bạn còn nợ {FormatVnd(totalDebt)}, không thể mượn sách.");
        }

        if (isLimitReached)
        {
            errors.Add($"Bạn đang mượn {currentLoans}/{maxBooks} sách, không thể mượn thêm.");
        }
        else if (isBatchExceeded)
        {
            errors.Add($"Bạn đang mượn {currentLoans}/{maxBooks} sách, không thể mượn thêm {bookIds.Count} sách vì vượt quá hạn mức ({maxBooks} sách).");
        }

        if (errors.Count > 0 && !bypassEligibility)
        {
            var errorMessage = string.Join(" ", errors);
            var operatorName = !string.IsNullOrWhiteSpace(actor) ? actor : "Thủ thư";
            var blockLog = new AuditLog
            {
                OccurredAtUtc = DateTime.UtcNow,
                Actor = operatorName,
                Action = AuditActions.BlockLoan,
                Target = $"Bạn đọc #{reader.Id} {reader.FullName} ({reader.Email}) – Lý do: {errorMessage}",
                IpAddress = "127.0.0.1"
            };
            db.AuditLogs.Add(blockLog);
            await db.SaveChangesAsync(cancellationToken);

            return new(false, errorMessage, []);
        }

        if (!bypassEligibility && !string.Equals(reader.Status, "Đang hoạt động", StringComparison.OrdinalIgnoreCase))
        {
            var operatorName = !string.IsNullOrWhiteSpace(actor) ? actor : "Thủ thư";
            var blockLog = new AuditLog
            {
                OccurredAtUtc = DateTime.UtcNow,
                Actor = operatorName,
                Action = AuditActions.BlockLoan,
                Target = $"Bạn đọc #{reader.Id} {reader.FullName} ({reader.Email}) – Lý do: Bạn đọc chưa ở trạng thái hoạt động.",
                IpAddress = "127.0.0.1"
            };
            db.AuditLogs.Add(blockLog);
            await db.SaveChangesAsync(cancellationToken);

            return new(false, "Bạn đọc chưa ở trạng thái hoạt động.", []);
        }

        var books = new List<Book>();
        foreach (var bookId in bookIds)
        {
            var book = await db.Books.FindAsync([bookId], cancellationToken);
            if (book == null) return new(false, "Không tìm thấy sách.", []);
            books.Add(book);
        }

        var originalDueDate = explicitOriginalDueDate ?? loanDate.AddDays(DefaultLoanDays);
        DateOnly dueDate;
        if (explicitDueDate is { } calculatedDueDate)
        {
            dueDate = calculatedDueDate;
        }
        else
        {
            try { dueDate = await AdjustDueDateAsync(originalDueDate, cancellationToken); }
            catch (InvalidOperationException exception) { return new(false, exception.Message, []); }
        }

        var createdLoans = new List<BookLoan>();
        foreach (var book in books)
        {
            var loan = new BookLoan
            {
                BookId = book.Id,
                ReaderAccountId = readerAccountId,
                LoanDate = loanDate,
                OriginalDueDate = originalDueDate,
                DueDate = dueDate,
                CreatedAtUtc = DateTime.UtcNow,
                Book = book,
                ReaderAccount = reader
            };
            db.BookLoans.Add(loan);
            createdLoans.Add(loan);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new(true, Loans: createdLoans);
    }

    public async Task<ReturnBookLoanOutcome> ReturnAsync(
        long loanId, DateTime returnedAtUtc, CancellationToken cancellationToken = default)
    {
        var returnedAt = returnedAtUtc.Kind switch
        {
            DateTimeKind.Utc => returnedAtUtc,
            DateTimeKind.Local => returnedAtUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(returnedAtUtc, DateTimeKind.Utc)
        };
        if (!db.Database.IsRelational())
        {
            var copyStatuses = (Available: BookCopyStatus.Available, Borrowed: BookCopyStatus.OnLoan, Held: BookCopyStatus.OnHold);
            var holdStatuses = (Pending: BookHoldStatus.Waiting, Assigned: BookHoldStatus.Available);
            var loan = await db.BookLoans.Include(item => item.BookCopy).Include(item => item.ReaderAccount)
                .FirstOrDefaultAsync(item => item.Id == loanId, cancellationToken);
            if (loan is null) return new(false, "Không tìm thấy phiếu mượn.", IsNotFound: true);
            if (loan.ReturnedAtUtc.HasValue || !string.IsNullOrEmpty(loan.CloseReason))
                return new(false, "Phiếu mượn này đã được trả hoặc đã đóng trước đó.", loan, IsConflict: true);
            if (loan.BookCopy is null) return new(false, "Phiếu mượn chưa được gán bản sao.", loan, IsConflict: true);
            if (!string.Equals(loan.BookCopy.Status, copyStatuses.Borrowed, StringComparison.OrdinalIgnoreCase))
                return new(false, "Trạng thái bản sao không hợp lệ để nhận trả.", loan, IsConflict: true);
            if (await db.ReaderFees.AnyAsync(fee => fee.BookLoanId == loanId, cancellationToken))
                return new(false, "Phiếu mượn đã có khoản phải thu; cần kiểm tra dữ liệu trước khi nhận trả.", loan, IsConflict: true);

            var inMemoryFeePolicy = await db.LoanPolicies.AsNoTracking()
                .SingleOrDefaultAsync(policy => policy.Id == LoanPolicy.SingletonId, cancellationToken);
            if (inMemoryFeePolicy?.DailyOverdueFee is not { } inMemoryDailyRate || inMemoryDailyRate < 0)
                return new(false, "Chưa cấu hình chính sách phí trễ hạn; chưa thể nhận trả.");

            var returnDate = LibraryCalendarDate.DateFromUtc(returnedAt);
            OverdueFeeCalculation inMemoryFeeCalculation;
            try { inMemoryFeeCalculation = await CalculateOverdueFeeAsync(loan.DueDate, returnDate, inMemoryDailyRate, cancellationToken); }
            catch (InvalidOperationException exception) { return new(false, exception.Message, loan); }

            var assignedHold = await FindNextPendingHoldAsync(loan.BookCopy.BookId, cancellationToken);
            DateTime? pickupDeadlineUtc = null;
            if (assignedHold != null)
            {
                try { pickupDeadlineUtc = await HoldPickupDeadlineCalculator.ThreeOpenDaysAfterAsync(returnedAt, this, cancellationToken); }
                catch (InvalidOperationException exception) { return new(false, exception.Message); }
            }
            loan.OverdueFee = inMemoryFeeCalculation.EstimatedFee;
            loan.OverdueDaysAtReturn = inMemoryFeeCalculation.OverdueDays;
            loan.DailyOverdueFeeAtReturn = inMemoryFeeCalculation.DailyRate;
            ReaderFee? inMemoryReceivable = null;
            if (loan.OverdueFee > 0m)
            {
                inMemoryReceivable = CreateOverdueReceivable(loan, returnedAt);
                if (loan.ReaderAccount is null)
                    return new(false, "Không tìm thấy bạn đọc của phiếu mượn.", loan, IsConflict: true);
                loan.ReaderAccount.OutstandingBalance += loan.OverdueFee;
                db.ReaderFees.Add(inMemoryReceivable);
            }
            loan.ReturnedAtUtc = returnedAt;
            loan.CloseReason = BookLoanCloseReason.Returned;
            loan.BookCopy.Status = assignedHold is null ? copyStatuses.Available : copyStatuses.Held;
            if (assignedHold is not null)
            {
                assignedHold.Status = holdStatuses.Assigned;
                assignedHold.BookCopyId = loan.BookCopy.Id;
                assignedHold.PickupDeadlineUtc = pickupDeadlineUtc;
            }
            await db.SaveChangesAsync(cancellationToken);
            return new(true, Loan: loan, AssignedHold: assignedHold, CopyStatus: loan.BookCopy.Status,
                OverdueDays: inMemoryFeeCalculation.OverdueDays, Receivable: inMemoryReceivable);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var currentLoan = await db.BookLoans.AsNoTracking().Include(item => item.BookCopy).Include(item => item.ReaderAccount)
            .FirstOrDefaultAsync(item => item.Id == loanId, cancellationToken);
        if (currentLoan is null) return new(false, "Không tìm thấy phiếu mượn.", IsNotFound: true);
        if (currentLoan.ReturnedAtUtc.HasValue || !string.IsNullOrEmpty(currentLoan.CloseReason))
            return new(false, "Phiếu mượn này đã được trả hoặc đã đóng trước đó.", currentLoan, IsConflict: true);
        if (currentLoan.BookCopyId is null || currentLoan.BookCopy is null)
            return new(false, "Phiếu mượn chưa được gán bản sao.", currentLoan, IsConflict: true);
        if (!string.Equals(currentLoan.BookCopy.Status, BookCopyStatus.OnLoan, StringComparison.OrdinalIgnoreCase))
            return new(false, "Trạng thái bản sao không hợp lệ để nhận trả.", currentLoan, IsConflict: true);
        if (await db.ReaderFees.AnyAsync(fee => fee.BookLoanId == loanId, cancellationToken))
            return new(false, "Phiếu mượn đã có khoản phải thu; cần kiểm tra dữ liệu trước khi nhận trả.", currentLoan, IsConflict: true);

        var feePolicy = await db.LoanPolicies.AsNoTracking()
            .SingleOrDefaultAsync(policy => policy.Id == LoanPolicy.SingletonId, cancellationToken);
        if (feePolicy?.DailyOverdueFee is not { } dailyRate || dailyRate < 0)
            return new(false, "Chưa cấu hình chính sách phí trễ hạn; chưa thể nhận trả.");

        var actualReturnDate = LibraryCalendarDate.DateFromUtc(returnedAt);
        OverdueFeeCalculation feeCalculation;
        try { feeCalculation = await CalculateOverdueFeeAsync(currentLoan.DueDate, actualReturnDate, dailyRate, cancellationToken); }
        catch (InvalidOperationException exception) { return new(false, exception.Message, currentLoan); }
        var overdueFee = feeCalculation.EstimatedFee;
        currentLoan.OverdueFee = overdueFee;
        currentLoan.OverdueDaysAtReturn = feeCalculation.OverdueDays;
        currentLoan.DailyOverdueFeeAtReturn = feeCalculation.DailyRate;
        ReaderFee? receivable = null;
        if (overdueFee > 0m)
        {
            if (currentLoan.ReaderAccount is null)
                return new(false, "Không tìm thấy bạn đọc của phiếu mượn.", currentLoan, IsConflict: true);
            receivable = CreateOverdueReceivable(currentLoan, returnedAt);
        }

        var nextHold = await FindNextPendingHoldAsync(currentLoan.BookCopy.BookId, cancellationToken);
        DateTime? nextPickupDeadlineUtc = null;
        if (nextHold != null)
        {
            try { nextPickupDeadlineUtc = await HoldPickupDeadlineCalculator.ThreeOpenDaysAfterAsync(returnedAt, this, cancellationToken); }
            catch (InvalidOperationException exception) { return new(false, exception.Message); }
        }
        if (db.Database.IsRelational())
        {
            var claimed = await db.BookLoans
                .Where(item => item.Id == loanId && item.BookCopyId == currentLoan.BookCopyId && item.ReturnedAtUtc == null && item.CloseReason == null)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.ReturnedAtUtc, returnedAt)
                    .SetProperty(item => item.CloseReason, BookLoanCloseReason.Returned)
                    .SetProperty(item => item.OverdueFee, overdueFee)
                    .SetProperty(item => item.OverdueDaysAtReturn, feeCalculation.OverdueDays)
                    .SetProperty(item => item.DailyOverdueFeeAtReturn, feeCalculation.DailyRate), cancellationToken);
            if (claimed != 1)
                return new(false, "Phiếu mượn này đã được trả hoặc đã đóng trước đó.", currentLoan, IsConflict: true);
        }
        else
        {
            var inMemLoan = await db.BookLoans.FirstOrDefaultAsync(l => l.Id == loanId, cancellationToken);
            if (inMemLoan == null || inMemLoan.ReturnedAtUtc != null || !string.IsNullOrEmpty(inMemLoan.CloseReason))
                return new(false, "Phiếu mượn này đã được trả hoặc đã đóng trước đó.", currentLoan, IsConflict: true);

            inMemLoan.ReturnedAtUtc = returnedAt;
            inMemLoan.CloseReason = BookLoanCloseReason.Returned;
            inMemLoan.OverdueFee = overdueFee;
            inMemLoan.OverdueDaysAtReturn = feeCalculation.OverdueDays;
            inMemLoan.DailyOverdueFeeAtReturn = feeCalculation.DailyRate;
            await db.SaveChangesAsync(cancellationToken);
        }

        if (nextHold is not null)
        {
            var holdClaimed = await db.BookHolds
                .Where(hold => hold.Id == nextHold.Id && hold.Status == BookHoldStatus.Waiting && hold.BookCopyId == null)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(hold => hold.Status, BookHoldStatus.Available)
                    .SetProperty(hold => hold.BookCopyId, currentLoan.BookCopyId)
                    .SetProperty(hold => hold.PickupDeadlineUtc, nextPickupDeadlineUtc), cancellationToken);
            if (holdClaimed != 1)
                throw new InvalidOperationException("Yêu cầu đặt giữ đầu hàng đợi vừa được xử lý bởi thao tác khác.");
        }

        var copyStatus = nextHold is null ? BookCopyStatus.Available : BookCopyStatus.OnHold;
        if (db.Database.IsRelational())
        {
            var copyUpdated = await db.BookCopies
                .Where(item => item.Id == currentLoan.BookCopyId && item.Status == BookCopyStatus.OnLoan)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, copyStatus), cancellationToken);
            if (copyUpdated != 1) throw new InvalidOperationException("Không thể cập nhật trạng thái bản sao.");
        }
        else
        {
            var inMemCopy = await db.BookCopies.FirstOrDefaultAsync(c => c.Id == currentLoan.BookCopyId, cancellationToken);
            if (inMemCopy != null)
            {
                inMemCopy.Status = copyStatus;
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        if (receivable is not null)
        {
            var balanceUpdated = await db.ReaderAccounts
                .Where(reader => reader.Id == currentLoan.ReaderAccountId)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    reader => reader.OutstandingBalance, reader => reader.OutstandingBalance + overdueFee), cancellationToken);
            if (balanceUpdated != 1) throw new InvalidOperationException("Không thể cập nhật số dư phải thu của bạn đọc.");
            db.ReaderFees.Add(receivable);
            await db.SaveChangesAsync(cancellationToken);
            currentLoan.ReaderAccount!.OutstandingBalance += overdueFee;
        }

        await transaction.CommitAsync(cancellationToken);
        currentLoan.ReturnedAtUtc = returnedAt;
        currentLoan.CloseReason = BookLoanCloseReason.Returned;
        currentLoan.OverdueFee = overdueFee;
        currentLoan.BookCopy.Status = copyStatus;

        var trackedLoan = db.ChangeTracker.Entries<BookLoan>().FirstOrDefault(e => e.Entity.Id == loanId)?.Entity;
        if (trackedLoan != null)
        {
            trackedLoan.ReturnedAtUtc = returnedAt;
            trackedLoan.CloseReason = BookLoanCloseReason.Returned;
            trackedLoan.OverdueFee = overdueFee;
        }

        if (nextHold is not null)
        {
            nextHold.Status = BookHoldStatus.Available;
            nextHold.BookCopyId = currentLoan.BookCopyId;
            nextHold.PickupDeadlineUtc = nextPickupDeadlineUtc;
        }
        return new(true, Loan: currentLoan, AssignedHold: nextHold, CopyStatus: copyStatus,
            OverdueDays: feeCalculation.OverdueDays, Receivable: receivable);
    }

    private static ReaderFee CreateOverdueReceivable(BookLoan loan, DateTime createdAtUtc) => new()
    {
        ReaderAccountId = loan.ReaderAccountId,
        BookLoanId = loan.Id,
        Amount = loan.OverdueFee,
        PaidAmount = 0m,
        IsPaid = false,
        Status = "Chưa thanh toán",
        Description = $"Phí trễ hạn phiếu mượn #{loan.Id}",
        CreatedAtUtc = createdAtUtc
    };

    public async Task<BookCopyLoanLookupOutcome> LookupByCopyCodeAsync(
        string copyCode, DateOnly today, CancellationToken cancellationToken = default)
    {
        var code = copyCode?.Trim();
        if (string.IsNullOrEmpty(code)) return new(BookCopyLoanLookupStatus.CopyNotFound);
        var copy = await db.BookCopies.AsNoTracking().Include(item => item.Book)
            .FirstOrDefaultAsync(item => item.CopyCode == code, cancellationToken);
        if (copy is null) return new(BookCopyLoanLookupStatus.CopyNotFound);

        var unassignedOpenLoan = await db.BookLoans.AsNoTracking().AnyAsync(
            loan => loan.BookId == copy.BookId && loan.BookCopyId == null && loan.ReturnedAtUtc == null && loan.CloseReason == null,
            cancellationToken);
        if (unassignedOpenLoan)
            return new(BookCopyLoanLookupStatus.UnassignedOpenLoan, CopyCode: copy.CopyCode);

        var openLoans = await db.BookLoans.AsNoTracking().Include(loan => loan.ReaderAccount)
            .Where(loan => loan.BookCopyId == copy.Id && loan.ReturnedAtUtc == null && loan.CloseReason == null)
            .OrderByDescending(loan => loan.CreatedAtUtc).ThenByDescending(loan => loan.Id)
            .Take(2).ToListAsync(cancellationToken);
        if (openLoans.Count > 1) return new(BookCopyLoanLookupStatus.Inconsistent, CopyCode: copy.CopyCode);
        if (openLoans.Count == 0) return new(BookCopyLoanLookupStatus.NotBorrowed, CopyCode: copy.CopyCode);

        var loan = openLoans[0];
        var dailyRate = await db.LoanPolicies.AsNoTracking()
            .Where(policy => policy.Id == LoanPolicy.SingletonId)
            .Select(policy => policy.DailyOverdueFee).SingleOrDefaultAsync(cancellationToken);
        if (dailyRate is null || dailyRate < 0)
            throw new InvalidOperationException("Chưa cấu hình chính sách phí trễ hạn.");
        var fee = await CalculateOverdueFeeAsync(loan.DueDate, today, dailyRate.Value, cancellationToken);
        return new(BookCopyLoanLookupStatus.Borrowed, loan.Id, copy.CopyCode,
            loan.ReaderAccount?.FullName ?? string.Empty, loan.DueDate, fee.OverdueDays, copy.Book?.Title ?? string.Empty,
            dailyRate.Value, fee.EstimatedFee);
    }

    private async Task<OverdueFeeCalculation> CalculateOverdueFeeAsync(
        DateOnly dueDate, DateOnly returnDate, decimal dailyRate, CancellationToken cancellationToken)
    {
        var openDates = returnDate <= dueDate
            ? (IReadOnlySet<DateOnly>)new HashSet<DateOnly>()
            : await workingScheduleService.GetConfiguredOpenDatesAsync(
                dueDate.AddDays(1), returnDate, cancellationToken);
        return OverdueFeeCalculator.Calculate(dueDate, returnDate, dailyRate, openDates);
    }

    private async Task<BookHold?> FindNextPendingHoldAsync(int bookId, CancellationToken cancellationToken) =>
        await db.BookHolds.Include(hold => hold.ReaderAccount)
            .Where(hold => hold.BookId == bookId && hold.Status == BookHoldStatus.Waiting && hold.BookCopyId == null &&
                hold.ReaderAccount != null && hold.ReaderAccount.Status == "Đang hoạt động")
            .OrderBy(hold => hold.HeldAtUtc).ThenBy(hold => hold.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<RenewBookLoanOutcome> RenewAsync(long loanId, DateOnly today, CancellationToken cancellationToken = default)
    {
        var loan = await db.BookLoans.Include(item => item.ReaderAccount)
            .ThenInclude(reader => reader!.LibraryCard)
            .ThenInclude(card => card!.LibraryCardType)
            .FirstOrDefaultAsync(item => item.Id == loanId, cancellationToken);
        if (loan == null) return new(false, "Không tìm thấy phiếu mượn.");
        if (loan.DueDate < today) return new(false, "Phiếu mượn đã quá hạn.");
        if (loan.IsReturned) return new(false, "Phiếu mượn đã đóng, không thể gia hạn.");
        var cardType = loan.ReaderAccount?.LibraryCard?.LibraryCardType;
        if (cardType == null) return new(false, "Bạn đọc chưa có loại thẻ hợp lệ.");
        if (loan.RenewalCount >= cardType.MaxRenewals)
            return new(false, "Bạn đã sử dụng hết số lần gia hạn cho phép của loại thẻ.");

        var hasOtherReaderHold = await db.BookHolds.AnyAsync(hold =>
            hold.BookId == loan.BookId && hold.ReaderAccountId != loan.ReaderAccountId &&
            BookHoldStatus.ActiveStatuses.Contains(hold.Status), cancellationToken);
        if (hasOtherReaderHold)
            return new(false,
                "Không thể gia hạn vì đầu sách đang có bạn đọc khác đặt giữ/xếp hàng.",
                ReasonCode: "OTHER_READER_HOLD");

        var hasOtherOverdueLoan = await db.BookLoans.AnyAsync(other =>
            other.ReaderAccountId == loan.ReaderAccountId && other.Id != loan.Id
            && other.ReturnedAtUtc == null && other.DueDate < today,
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
        var renewalDays = cardType.LoanDays is > 0 ? cardType.LoanDays.Value : DefaultRenewalDays;
        try { newDueDate = await AdjustDueDateAsync(oldDueDate.AddDays(renewalDays), cancellationToken); }
        catch (InvalidOperationException exception) { return new(false, exception.Message); }

        loan.DueDate = newDueDate;
        loan.RenewalCount++;
        await db.SaveChangesAsync(cancellationToken);
        return new(true, Loan: loan, OldDueDate: oldDueDate);
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

    public static string FormatVnd(decimal amount)
    {
        var culture = new System.Globalization.CultureInfo("vi-VN");
        return amount % 1 == 0
            ? $"{amount.ToString("#,##0", culture)} VND"
            : $"{amount.ToString("#,##0.##", culture)} VND";
    }

    public async Task<BookLoanOutcome> OverrideCreateAsync(
        int bookId, int readerAccountId, DateOnly loanDate, string actor, string bypassReason, CancellationToken cancellationToken = default)
    {
        var result = await OverrideCreateManyAsync([bookId], readerAccountId, loanDate, actor, bypassReason, cancellationToken);
        if (!result.IsSuccess)
        {
            return new(false, result.ErrorMessage);
        }
        return new(true, Loan: result.Loans.FirstOrDefault());
    }

    public async Task<BatchBookLoanOutcome> OverrideCreateManyAsync(
        IReadOnlyList<int> bookIds, int readerAccountId, DateOnly loanDate, string actor, string bypassReason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bypassReason))
        {
            return new(false, "Vui lòng nhập lý do bỏ qua.", []);
        }

        if (bookIds == null || bookIds.Count == 0)
        {
            return new(false, "Vui lòng chọn sách.", []);
        }

        var reader = await db.ReaderAccounts
            .Include(item => item.LibraryCard)
                .ThenInclude(card => card!.LibraryCardType)
            .SingleOrDefaultAsync(item => item.Id == readerAccountId, cancellationToken);

        if (reader is null)
        {
            return new(false, "Không tìm thấy bạn đọc.", []);
        }

        var checkDate = loanDate;
        var isCardLocked = reader.IsLocked
            || (reader.LibraryCard != null && string.Equals(reader.LibraryCard.Status, "Bị khoá", StringComparison.OrdinalIgnoreCase))
            || (reader.LibraryCard != null && reader.LibraryCard.IsLocked)
            || reader.Status.Contains("khóa", StringComparison.OrdinalIgnoreCase)
            || reader.Status.Contains("khoá", StringComparison.OrdinalIgnoreCase);

        var isCardExpired = reader.LibraryCard != null && reader.LibraryCard.ExpiresOn < checkDate;

        var readerLoans = await db.BookLoans
            .Where(l => l.ReaderAccountId == readerAccountId)
            .ToListAsync(cancellationToken);

        var hasOverdueLoan = readerLoans.Any(l => !l.IsReturned && l.DueDate < checkDate);

        var totalDebt = reader.TotalDebt;
        var hasUnpaidFee = totalDebt > 0m;

        var maxBooks = reader.LibraryCard?.LibraryCardType?.MaxBooks ?? LibraryCardType.DefaultMaxBooks;
        var currentLoans = readerLoans.Count(l => !l.IsReturned);

        var isLimitReached = currentLoans >= maxBooks;
        var isBatchExceeded = !isLimitReached && (currentLoans + bookIds.Count > maxBooks);

        var errors = new List<string>();

        if (isCardLocked)
        {
            errors.Add("Thẻ bạn đọc đang bị khoá.");
        }

        if (isCardExpired)
        {
            errors.Add("Thẻ bạn đọc đã hết hạn.");
        }

        if (hasOverdueLoan)
        {
            errors.Add("Bạn đọc đang có phiếu mượn quá hạn chưa trả.");
        }

        if (hasUnpaidFee)
        {
            errors.Add($"Bạn còn nợ {FormatVnd(totalDebt)}, không thể mượn sách.");
        }

        if (isLimitReached)
        {
            errors.Add($"Bạn đang mượn {currentLoans}/{maxBooks} sách, không thể mượn thêm.");
        }
        else if (isBatchExceeded)
        {
            errors.Add($"Bạn đang mượn {currentLoans}/{maxBooks} sách, không thể mượn thêm {bookIds.Count} sách vì vượt quá hạn mức ({maxBooks} sách).");
        }

        if (!string.Equals(reader.Status, "Đang hoạt động", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Bạn đọc chưa ở trạng thái hoạt động.");
        }

        var initialReason = errors.Count > 0 ? string.Join(" ", errors) : "Không có vi phạm";
        var operatorName = !string.IsNullOrWhiteSpace(actor) ? actor : "Quản lý thư viện";

        var overrideLog = new AuditLog
        {
            OccurredAtUtc = DateTime.UtcNow,
            Actor = operatorName,
            Action = AuditActions.OverrideBlockLoan,
            Target = $"Bạn đọc #{reader.Id} {reader.FullName} ({reader.Email}) – Lý do chặn: {initialReason} – Lý do bỏ qua: {bypassReason.Trim()}",
            IpAddress = "127.0.0.1"
        };
        db.AuditLogs.Add(overrideLog);
        await db.SaveChangesAsync(cancellationToken);

        var books = new List<Book>();
        foreach (var bookId in bookIds)
        {
            var book = await db.Books.FindAsync([bookId], cancellationToken);
            if (book == null) return new(false, "Không tìm thấy sách.", []);
            books.Add(book);
        }

        var originalDueDate = loanDate.AddDays(DefaultLoanDays);
        DateOnly dueDate;
        try { dueDate = await AdjustDueDateAsync(originalDueDate, cancellationToken); }
        catch (InvalidOperationException exception) { return new(false, exception.Message, []); }

        var createdLoans = new List<BookLoan>();
        foreach (var book in books)
        {
            var loan = new BookLoan
            {
                BookId = book.Id,
                ReaderAccountId = readerAccountId,
                LoanDate = loanDate,
                OriginalDueDate = originalDueDate,
                DueDate = dueDate,
                CreatedAtUtc = DateTime.UtcNow,
                Book = book,
                ReaderAccount = reader
            };
            db.BookLoans.Add(loan);
            createdLoans.Add(loan);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new(true, Loans: createdLoans);
    }

    public async Task<IReadOnlyList<BlockedLoanLogEntry>> GetBlockedLoanLogsAsync(
        int? readerAccountId = null, CancellationToken cancellationToken = default)
    {
        var logs = await db.AuditLogs.AsNoTracking()
            .Where(l => l.Action == AuditActions.BlockLoan || l.Action == AuditActions.OverrideBlockLoan)
            .OrderByDescending(l => l.OccurredAtUtc)
            .ToListAsync(cancellationToken);

        var result = new List<BlockedLoanLogEntry>();
        foreach (var log in logs)
        {
            var entry = ToBlockedLoanLogEntry(log);
            if (readerAccountId == null || entry.ReaderAccountId == readerAccountId.Value)
            {
                result.Add(entry);
            }
        }
        return result;
    }

    public static BlockedLoanLogEntry ToBlockedLoanLogEntry(AuditLog log)
    {
        var entry = new BlockedLoanLogEntry
        {
            Id = log.Id,
            OccurredAtUtc = log.OccurredAtUtc,
            Operator = log.Actor,
            Target = log.Target
        };

        if (log.Action == AuditActions.OverrideBlockLoan)
        {
            entry.Status = "Đã bỏ qua";
            const string blockedPrefix = " – Lý do chặn: ";
            const string bypassPrefix = " – Lý do bỏ qua: ";
            var blockIdx = log.Target.IndexOf(blockedPrefix, StringComparison.Ordinal);
            var bypassIdx = log.Target.IndexOf(bypassPrefix, StringComparison.Ordinal);

            if (blockIdx >= 0 && bypassIdx > blockIdx)
            {
                var readerPart = log.Target[..blockIdx].Trim();
                entry.Reason = log.Target[(blockIdx + blockedPrefix.Length)..bypassIdx].Trim();
                entry.BypassReason = log.Target[(bypassIdx + bypassPrefix.Length)..].Trim();
                ParseReaderPart(readerPart, entry);
            }
            else
            {
                entry.Reason = log.Target;
            }
        }
        else
        {
            entry.Status = "Bị chặn";
            const string reasonPrefix = " – Lý do: ";
            var reasonIdx = log.Target.IndexOf(reasonPrefix, StringComparison.Ordinal);
            if (reasonIdx >= 0)
            {
                entry.Reason = log.Target[(reasonIdx + reasonPrefix.Length)..].Trim();
                var readerPart = log.Target[..reasonIdx].Trim();
                ParseReaderPart(readerPart, entry);
            }
            else
            {
                entry.Reason = log.Target;
            }
        }

        return entry;
    }

    private static void ParseReaderPart(string readerPart, BlockedLoanLogEntry entry)
    {
        var match = System.Text.RegularExpressions.Regex.Match(readerPart, @"Bạn đọc #(\d+)\s+([^(]+)(?:\(([^)]+)\))?");
        if (match.Success)
        {
            if (int.TryParse(match.Groups[1].Value, out var id))
                entry.ReaderAccountId = id;
            entry.ReaderName = match.Groups[2].Value.Trim();
            if (match.Groups.Count > 3)
                entry.ReaderEmail = match.Groups[3].Value.Trim();
        }
        else
        {
            entry.ReaderName = readerPart;
        }
    }
}
