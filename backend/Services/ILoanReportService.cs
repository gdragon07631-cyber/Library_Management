using Project.Models;

namespace Project.Services;

public interface ILoanReportService
{
    LoanReportViewModel GetDemoSummary(DateOnly startDate, DateOnly endDate, DateOnly today);
    Task<IReadOnlyList<TopBookReportRow>> GetTopBooksAsync(
        DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TopHeldBookReportRow>> GetTopHeldBooksAsync(
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DailyLoanReportRow>> GetDailyLoansAsync(
        DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);
}
