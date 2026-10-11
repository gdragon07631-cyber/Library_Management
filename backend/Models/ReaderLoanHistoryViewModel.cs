namespace Project.Models;

public sealed class ReaderLoanHistoryViewModel
{
    public required ReaderAccount Reader { get; init; }
    public IReadOnlyList<BookLoan> Loans { get; init; } = [];
    public int OpenLoanCount { get; init; }
    public int TotalLoanCount { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public bool IsFiltered => StartDate.HasValue || EndDate.HasValue;
    public string? DateFilterError { get; init; }

    // The current schema does not persist a return date, so late returns cannot be calculated.
    public int? LateReturnCount { get; init; }
}
