namespace Project.Models;

public sealed record LoanReportViewModel(
    DateOnly StartDate,
    DateOnly EndDate,
    int TotalLoans,
    int TotalReturns,
    int OpenLoans,
    int OverdueLoans,
    decimal OverdueRate,
    bool HasActivityInRange);

public sealed record TopBookReportRow(
    int Rank,
    int BookId,
    string BookTitle,
    int LoanCount,
    int PhysicalCopyCount);

public sealed record TopHeldBookReportRow(
    int Rank,
    int BookId,
    string BookTitle,
    int PendingHoldCount);

public sealed record DailyLoanReportRow(DateOnly Date, int LoanCount);

public sealed class LoanReportPageViewModel
{
    public required LoanReportViewModel Summary { get; init; }
    public IReadOnlyList<TopBookReportRow> TopBooks { get; init; } = [];
    public IReadOnlyList<TopHeldBookReportRow> TopHeldBooks { get; init; } = [];
    public IReadOnlyList<DailyLoanReportRow> DailyLoans { get; init; } = [];
}
