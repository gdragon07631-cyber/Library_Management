using Project.Models;

namespace Project.Services;

public interface ILoanReportService
{
    LoanReportViewModel GetDemoReport(DateOnly startDate, DateOnly endDate, DateOnly today);
    Task<IReadOnlyList<TopBookReportRow>> GetTopBooksAsync(
        DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);
}
