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

    public int LateReturnCount { get; init; }
}
