using Project.Models;

namespace Project.Services;

public interface ILoanReportService
{
<<<<<<< HEAD:backend/Services/ILoanReportService.cs
<<<<<<< HEAD:backend/Services/ILoanReportService.cs
<<<<<<< HEAD:backend/Services/ILoanReportService.cs
    LoanReportViewModel GetDemoReport(DateOnly startDate, DateOnly endDate, DateOnly today);
=======
=======
>>>>>>> origin/feature/S4-07/xem_bieu_do_luot_muon_theo_ngay:Services/ILoanReportService.cs
    LoanReportViewModel GetDemoSummary(DateOnly startDate, DateOnly endDate, DateOnly today);
    Task<IReadOnlyList<TopBookReportRow>> GetTopBooksAsync(
        DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TopHeldBookReportRow>> GetTopHeldBooksAsync(CancellationToken cancellationToken = default);
<<<<<<< HEAD:backend/Services/ILoanReportService.cs
>>>>>>> origin/feature/S4-07/xem_10_dau_sach_co_hang_doi_dat_giu:Services/ILoanReportService.cs
=======
    LoanReportViewModel GetDemoReport(DateOnly startDate, DateOnly endDate, DateOnly today);
    Task<IReadOnlyList<TopBookReportRow>> GetTopBooksAsync(
        DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);
>>>>>>> origin/feature/S4-07/xem_10_dau_sach_duoc_muon:Services/ILoanReportService.cs
=======
    Task<IReadOnlyList<DailyLoanReportRow>> GetDailyLoansAsync(
        DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);
>>>>>>> origin/feature/S4-07/xem_bieu_do_luot_muon_theo_ngay:Services/ILoanReportService.cs
}
