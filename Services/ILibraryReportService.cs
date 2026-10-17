using Project.Models;

namespace Project.Services;

public interface ILibraryReportService
{
    Task<LoanReportSummary> GetSummaryAsync(DateOnly startDate, DateOnly endDate, DateOnly today, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TopBorrowedBook>> GetTopBorrowedBooksAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TopHeldBook>> GetTopHeldBooksAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DailyLoanCount>> GetDailyLoansAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);
}
