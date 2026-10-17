namespace Project.Models;

public sealed record LoanReportSummary(int TotalLoans, int TotalReturns, int OpenLoans, int OverdueLoans, decimal OverdueRate);
public sealed record TopBorrowedBook(int BookId, string Title, int LoanCount, int PhysicalCopyCount);
public sealed record TopHeldBook(int BookId, string Title, int HoldCount);
public sealed record DailyLoanCount(DateOnly Date, int Count);

public sealed class ReportPageViewModel
{
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
}
