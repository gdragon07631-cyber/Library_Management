using Project.Models;
using System.Globalization;

namespace Project.Services;

public sealed record BookLoanOutcome(
    bool IsSuccess, string? ErrorMessage = null, BookLoan? Loan = null,
    int? BorrowedCount = null, int? BorrowingLimit = null, string? ReasonCode = null,
    IReadOnlyList<string>? BlockingReasons = null, decimal? OutstandingBalance = null);
public sealed record BorrowingPolicyViolation(string Code, string Message);
public sealed record ReaderBorrowingStatus(
    int ReaderAccountId, string ReaderCode, string ReaderName, string CardTypeName,
    int BorrowedCount, int BorrowingLimit, string? CardStatus, DateOnly? CardExpiresOn,
    int OverdueLoanCount, decimal OutstandingBalance,
    IReadOnlyList<BorrowingPolicyViolation> BlockingReasons)
{
    public int Remaining => Math.Max(0, BorrowingLimit - BorrowedCount);
    public bool IsBlocked => BlockingReasons.Count > 0;
    public string OutstandingBalanceFormatted => FormatVnd(OutstandingBalance);
    public string? Message => IsBlocked
        ? string.Join(" ", BlockingReasons.Select(reason => reason.Message))
        : null;

    public static string FormatVnd(decimal amount) =>
        amount.ToString(amount == decimal.Truncate(amount) ? "N0" : "N2",
            CultureInfo.GetCultureInfo("vi-VN"));
}
public sealed record RenewBookLoanOutcome(
    bool IsSuccess, string? ErrorMessage = null, BookLoan? Loan = null,
    DateOnly? OldDueDate = null, string? ReasonCode = null);
public sealed record ReturnBookLoanOutcome(bool IsSuccess, string? ErrorMessage = null, BookLoan? Loan = null);

public interface IBookLoanService
{
    Task<IReadOnlyList<BookLoan>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<ReaderBorrowingStatus?> GetBorrowingStatusAsync(int readerAccountId, CancellationToken cancellationToken = default);
    Task<BookLoanOutcome> CreateAsync(
        int bookId, int readerAccountId, DateOnly loanDate, CancellationToken cancellationToken = default,
        string? actor = null, string? ipAddress = null, string? overrideReason = null,
        Guid? overrideRequestId = null, string? actorRole = null);
    Task<RenewBookLoanOutcome> RenewAsync(long loanId, DateOnly today, CancellationToken cancellationToken = default);
    Task<ReturnBookLoanOutcome> ReturnAsync(
        long loanId, string? actor, string? ipAddress, CancellationToken cancellationToken = default);
    Task<DateOnly> AdjustDueDateAsync(DateOnly proposedDate, CancellationToken cancellationToken = default);
}
