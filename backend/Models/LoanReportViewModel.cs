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
<<<<<<< HEAD:backend/Models/LoanReportViewModel.cs
=======
=======
>>>>>>> origin/feature/S4-07/xem_10_dau_sach_duoc_muon:Models/LoanReportViewModel.cs

public sealed record TopBookReportRow(
    int Rank,
    int BookId,
    string BookTitle,
    int LoanCount,
    int PhysicalCopyCount);

<<<<<<< HEAD:backend/Models/LoanReportViewModel.cs
public sealed record TopHeldBookReportRow(
    int Rank,
    int BookId,
    string BookTitle,
    int PendingHoldCount);

=======
>>>>>>> origin/feature/S4-07/xem_10_dau_sach_duoc_muon:Models/LoanReportViewModel.cs
public sealed class LoanReportPageViewModel
{
    public required LoanReportViewModel Summary { get; init; }
    public IReadOnlyList<TopBookReportRow> TopBooks { get; init; } = [];
<<<<<<< HEAD:backend/Models/LoanReportViewModel.cs
    public IReadOnlyList<TopHeldBookReportRow> TopHeldBooks { get; init; } = [];
}
>>>>>>> origin/feature/S4-07/xem_10_dau_sach_co_hang_doi_dat_giu:Models/LoanReportViewModel.cs
=======
}
>>>>>>> origin/feature/S4-07/xem_10_dau_sach_duoc_muon:Models/LoanReportViewModel.cs
