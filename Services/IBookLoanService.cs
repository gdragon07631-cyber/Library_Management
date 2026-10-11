using Project.Models;

namespace Project.Services;

public sealed record BookLoanOutcome(bool IsSuccess, string? ErrorMessage = null, BookLoan? Loan = null);
public sealed record RenewBookLoanOutcome(
    bool IsSuccess, string? ErrorMessage = null, BookLoan? Loan = null,
    DateOnly? OldDueDate = null, string? ReasonCode = null);
public enum BookCopyLoanLookupStatus { CopyNotFound, NotBorrowed, Borrowed, Inconsistent, UnassignedOpenLoan }
public sealed record BookCopyLoanLookupOutcome(
    BookCopyLoanLookupStatus Status, long? LoanId = null, string? CopyCode = null,
    string? ReaderName = null, DateOnly? DueDate = null, int OverdueDays = 0);
public sealed record ReturnBookLoanOutcome(
    bool IsSuccess, string? ErrorMessage = null, BookLoan? Loan = null,
    bool IsNotFound = false, bool IsConflict = false);

public interface IBookLoanService
{
    Task<IReadOnlyList<BookLoan>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<BookLoanOutcome> CreateAsync(int bookId, int readerAccountId, DateOnly loanDate, CancellationToken cancellationToken = default, long? bookCopyId = null);
    Task<RenewBookLoanOutcome> RenewAsync(long loanId, DateOnly today, CancellationToken cancellationToken = default);
    Task<ReturnBookLoanOutcome> ReturnAsync(long loanId, DateTime returnedAtUtc, CancellationToken cancellationToken = default);
    Task<BookCopyLoanLookupOutcome> LookupByCopyCodeAsync(string copyCode, DateOnly today, CancellationToken cancellationToken = default);
    Task<DateOnly> AdjustDueDateAsync(DateOnly proposedDate, CancellationToken cancellationToken = default);
}
