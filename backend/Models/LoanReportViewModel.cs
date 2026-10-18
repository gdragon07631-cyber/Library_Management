namespace Project.Models;

/// <summary>Summary metrics for the practice loan, return, and overdue report.</summary>
public sealed record LoanReportViewModel(
    DateOnly StartDate,
    DateOnly EndDate,
    int TotalLoans,
    int TotalReturns,
    int OpenLoans,
    int OverdueLoans,
    decimal OverdueRate,
    bool HasActivityInRange);

/// <summary>One ranked book title in the top borrowed books table.</summary>
public sealed record TopBookReportRow(
    int Rank,
    int BookId,
    string BookTitle,
    int LoanCount,
    int PhysicalCopyCount);

/// <summary>One ranked book title in the current hold queue table.</summary>
public sealed record TopHeldBookReportRow(
    int Rank,
    int BookId,
    string BookTitle,
    int PendingHoldCount);

/// <summary>One calendar day and its loan count in the daily loans chart.</summary>
public sealed record DailyLoanReportRow(
    DateOnly Date,
    int LoanCount);

/// <summary>Initial view model for the report page.</summary>
public sealed record LoanReportPageViewModel(
    LoanReportViewModel Summary,
    IReadOnlyList<TopBookReportRow> TopBooks,
    IReadOnlyList<TopHeldBookReportRow> TopHeldBooks,
    IReadOnlyList<DailyLoanReportRow> DailyLoans);
