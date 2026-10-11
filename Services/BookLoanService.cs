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
            .Include(loan => loan.BookCopy)
            .Include(loan => loan.ReaderAccount).ThenInclude(reader => reader!.LibraryCard)
                .ThenInclude(card => card!.LibraryCardType)
            .OrderByDescending(loan => loan.CreatedAtUtc).ToListAsync(cancellationToken);

    public async Task<BookLoanOutcome> CreateAsync(int bookId, int readerAccountId, DateOnly loanDate, CancellationToken cancellationToken = default, long? bookCopyId = null)
    {
        var book = await db.Books.FindAsync([bookId], cancellationToken);
        if (book == null) return new(false, "Không tìm thấy sách.");
        BookCopy? copy = null;
        if (bookCopyId.HasValue)
        {
            copy = await db.BookCopies.FirstOrDefaultAsync(item => item.Id == bookCopyId.Value, cancellationToken);
            if (copy == null) return new(false, "Không tìm thấy bản sao sách.");
            if (copy.BookId != bookId) return new(false, "Bản sao không thuộc sách đã chọn.");
            if (!string.Equals(copy.Status, BookCopyStatuses.Available, StringComparison.OrdinalIgnoreCase))
                return new(false, "Bản sao này hiện không sẵn sàng cho mượn.");
            if (await db.BookLoans.AnyAsync(loan => loan.BookCopyId == copy.Id && loan.ReturnedAtUtc == null, cancellationToken))
                return new(false, "Bản sao này đã có phiếu mượn đang mở.");
            if (await db.BookLoans.AnyAsync(loan => loan.BookId == bookId && loan.BookCopyId == null && loan.ReturnedAtUtc == null, cancellationToken))
                return new(false, "Sách còn phiếu mượn cũ chưa gán bản sao. Hãy xử lý các phiếu đó trước khi gán bản sao cho phiếu mới.");
        }
        var reader = await db.ReaderAccounts.FindAsync([readerAccountId], cancellationToken);
        if (reader == null) return new(false, "Không tìm thấy bạn đọc.");
        if (!string.Equals(reader.Status, "Đang hoạt động", StringComparison.OrdinalIgnoreCase))
            return new(false, "Bạn đọc chưa ở trạng thái hoạt động.");

        var originalDueDate = loanDate.AddDays(DefaultLoanDays);
        DateOnly dueDate;
        try { dueDate = await AdjustDueDateAsync(originalDueDate, cancellationToken); }
        catch (InvalidOperationException exception) { return new(false, exception.Message); }

        var loan = new BookLoan
        {
            BookId = bookId,
            BookCopyId = copy?.Id,
            ReaderAccountId = readerAccountId,
            LoanDate = loanDate,
            OriginalDueDate = originalDueDate,
            DueDate = dueDate,
            CreatedAtUtc = DateTime.UtcNow,
            Book = book,
            BookCopy = copy,
            ReaderAccount = reader
        };
        if (copy is not null) copy.Status = BookCopyStatuses.Borrowed;
        db.BookLoans.Add(loan);
        await db.SaveChangesAsync(cancellationToken);
        return new(true, Loan: loan);
    }

    public async Task<RenewBookLoanOutcome> RenewAsync(long loanId, DateOnly today, CancellationToken cancellationToken = default)
    {
        var loan = await db.BookLoans.Include(item => item.ReaderAccount)
            .ThenInclude(reader => reader!.LibraryCard)
            .ThenInclude(card => card!.LibraryCardType)
            .FirstOrDefaultAsync(item => item.Id == loanId, cancellationToken);
        if (loan == null) return new(false, "Không tìm thấy phiếu mượn.");
        if (loan.ReturnedAtUtc.HasValue) return new(false, "Phiếu mượn này đã được xác nhận trả.");
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

    public async Task<BookLoanOutcome> ReturnAsync(long loanId, DateTime returnedAtUtc, CancellationToken cancellationToken = default)
    {
        var loan = await db.BookLoans.Include(item => item.BookCopy)
            .FirstOrDefaultAsync(item => item.Id == loanId, cancellationToken);
        if (loan == null) return new(false, "Không tìm thấy phiếu mượn.");
        if (loan.ReturnedAtUtc.HasValue) return new(false, "Phiếu mượn này đã được xác nhận trả.", loan);

        loan.ReturnedAtUtc = returnedAtUtc.Kind switch
        {
            DateTimeKind.Utc => returnedAtUtc,
            DateTimeKind.Local => returnedAtUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(returnedAtUtc, DateTimeKind.Utc)
        };
        if (loan.BookCopy is not null) loan.BookCopy.Status = BookCopyStatuses.Available;
        await db.SaveChangesAsync(cancellationToken);
        return new(true, Loan: loan);
    }

    public async Task<BookCopyLoanLookupOutcome> LookupByCopyCodeAsync(
        string copyCode, DateOnly today, CancellationToken cancellationToken = default)
    {
        var normalizedCode = copyCode.Trim();
        var copy = await db.BookCopies.AsNoTracking()
            .FirstOrDefaultAsync(item => item.CopyCode == normalizedCode, cancellationToken);
        if (copy is null) return new(BookCopyLoanLookupStatus.CopyNotFound);

        if (await db.BookLoans.AnyAsync(loan =>
                loan.BookId == copy.BookId && loan.BookCopyId == null && loan.ReturnedAtUtc == null,
                cancellationToken))
            return new(BookCopyLoanLookupStatus.UnassignedOpenLoan, CopyCode: copy.CopyCode);

        var openLoans = await db.BookLoans.AsNoTracking()
            .Where(loan => loan.BookCopyId == copy.Id && loan.ReturnedAtUtc == null)
            .Include(loan => loan.ReaderAccount)
            .Take(2)
            .ToListAsync(cancellationToken);
        if (openLoans.Count > 1)
            return new(BookCopyLoanLookupStatus.Inconsistent, CopyCode: copy.CopyCode);
        if (openLoans.Count == 0)
            return new(BookCopyLoanLookupStatus.NotBorrowed, CopyCode: copy.CopyCode);

        var openLoan = openLoans[0];
        return new(BookCopyLoanLookupStatus.Borrowed,
            CopyCode: copy.CopyCode,
            ReaderName: openLoan.ReaderAccount?.FullName,
            DueDate: openLoan.DueDate,
            OverdueDays: Math.Max(0, today.DayNumber - openLoan.DueDate.DayNumber));
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
