using Project.Models;

namespace Project.Services;

public sealed record BookLoanOutcome(bool IsSuccess, string? ErrorMessage = null, BookLoan? Loan = null);
public sealed record BatchBookLoanOutcome(bool IsSuccess, string? ErrorMessage = null, IReadOnlyList<BookLoan> Loans = null!);
public sealed record RenewBookLoanOutcome(
    bool IsSuccess, string? ErrorMessage = null, BookLoan? Loan = null,
    DateOnly? OldDueDate = null, string? ReasonCode = null);
public enum BookCopyLoanLookupStatus { CopyNotFound, NotBorrowed, Borrowed, Inconsistent, UnassignedOpenLoan }
public sealed record BookCopyLoanLookupOutcome(
    BookCopyLoanLookupStatus Status, long? LoanId = null, string? CopyCode = null,
    string? ReaderName = null, DateOnly? DueDate = null, int OverdueDays = 0, string? BookTitle = null,
    decimal DailyOverdueFee = 0m, decimal EstimatedOverdueFee = 0m);
public sealed record ReturnBookLoanOutcome(
    bool IsSuccess, string? ErrorMessage = null, BookLoan? Loan = null,
    bool IsNotFound = false, bool IsConflict = false,
    BookHold? AssignedHold = null, string? CopyStatus = null, int OverdueDays = 0, ReaderFee? Receivable = null);

public interface IBookLoanService
{
    Task<IReadOnlyList<BookLoan>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OverdueLoanItem>> GetOverdueAsync(DateOnly today, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OverdueLoanItem>> GetOverdueAsync(DateOnly today, OverdueLoanRange range, CancellationToken cancellationToken = default);
    Task<BookLoanOutcome> CreateAsync(int bookId, int readerAccountId, DateOnly loanDate, CancellationToken cancellationToken = default);
    Task<BookLoanOutcome> CreateAsync(int bookId, int readerAccountId, DateOnly loanDate, string? actor, CancellationToken cancellationToken = default);
    Task<BookLoanOutcome> CreateForHoldAsync(int bookId, int readerAccountId, DateOnly loanDate, DateOnly originalDueDate, DateOnly dueDate, string? actor, CancellationToken cancellationToken = default);
    Task<BookLoanOutcome> OverrideCreateForHoldAsync(int bookId, int readerAccountId, DateOnly loanDate, DateOnly originalDueDate, DateOnly dueDate, string actor, CancellationToken cancellationToken = default) =>
        CreateForHoldAsync(bookId, readerAccountId, loanDate, originalDueDate, dueDate, actor, cancellationToken);
    Task<BatchBookLoanOutcome> CreateManyAsync(IReadOnlyList<int> bookIds, int readerAccountId, DateOnly loanDate, CancellationToken cancellationToken = default);
    Task<BatchBookLoanOutcome> CreateManyAsync(IReadOnlyList<int> bookIds, int readerAccountId, DateOnly loanDate, string? actor, CancellationToken cancellationToken = default);
    Task<RenewBookLoanOutcome> RenewAsync(long loanId, DateOnly today, CancellationToken cancellationToken = default);
    Task<DateOnly> AdjustDueDateAsync(DateOnly proposedDate, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BlockedLoanLogEntry>> GetBlockedLoanLogsAsync(int? readerAccountId = null, CancellationToken cancellationToken = default);
    Task<BookLoanOutcome> OverrideCreateAsync(int bookId, int readerAccountId, DateOnly loanDate, string actor, string bypassReason, CancellationToken cancellationToken = default);
    Task<BatchBookLoanOutcome> OverrideCreateManyAsync(IReadOnlyList<int> bookIds, int readerAccountId, DateOnly loanDate, string actor, string bypassReason, CancellationToken cancellationToken = default);
    Task<ReturnBookLoanOutcome> ReturnAsync(long loanId, DateTime returnedAtUtc, CancellationToken cancellationToken = default);
    Task<BookCopyLoanLookupOutcome> LookupByCopyCodeAsync(string copyCode, DateOnly today, CancellationToken cancellationToken = default);
}
