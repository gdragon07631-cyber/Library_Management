namespace Project.Models;

public sealed class ReaderLoanHistoryViewModel
{
    public required ReaderAccount Reader { get; init; }
    public IReadOnlyList<BookLoan> Loans { get; init; } = [];
    public int OpenLoanCount { get; init; }
    public int TotalLoanCount { get; init; }

    // The current schema does not persist a return date, so late returns cannot be calculated.
    public int? LateReturnCount { get; init; }
}
