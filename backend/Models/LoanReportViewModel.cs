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
<<<<<<< HEAD:backend/Models/LoanReportViewModel.cs
=======

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

public sealed class LoanReportPageViewModel
{
    public required LoanReportViewModel Summary { get; init; }
    public IReadOnlyList<TopBookReportRow> TopBooks { get; init; } = [];
    public IReadOnlyList<TopHeldBookReportRow> TopHeldBooks { get; init; } = [];
}
>>>>>>> origin/feature/S4-07/xem_10_dau_sach_co_hang_doi_dat_giu:Models/LoanReportViewModel.cs
