namespace Project.Models;

public sealed class ReaderLoanHistoryPageViewModel
{
    public IReadOnlyList<BookLoan> Loans { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; } = 20;
    public int TotalItems { get; init; }
    public int TotalPages => TotalItems == 0 ? 0 : (int)Math.Ceiling(TotalItems / (double)PageSize);
}

public sealed record ReaderLoanDetailsResponse(
    long LoanId,
    string BookTitle,
    string? CopyBarcode,
    DateOnly LoanDate,
    DateOnly DueDate,
    bool IsReturned,
    DateTime? ReturnedAtUtc);
