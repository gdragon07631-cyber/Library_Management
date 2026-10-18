using Project.Models;

namespace Project.Services;

public interface ILoanReportService
{
<<<<<<< HEAD:backend/Services/ILoanReportService.cs
    LoanReportViewModel GetDemoReport(DateOnly startDate, DateOnly endDate, DateOnly today);
=======
    LoanReportViewModel GetDemoSummary(DateOnly startDate, DateOnly endDate, DateOnly today);
    Task<IReadOnlyList<TopBookReportRow>> GetTopBooksAsync(
        DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TopHeldBookReportRow>> GetTopHeldBooksAsync(CancellationToken cancellationToken = default);
>>>>>>> origin/feature/S4-07/xem_10_dau_sach_co_hang_doi_dat_giu:Services/ILoanReportService.cs
}
