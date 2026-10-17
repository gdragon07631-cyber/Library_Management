using Project.Models;

namespace Project.Services;

public interface ILoanReportService
{
    LoanReportViewModel GetDemoReport(DateOnly startDate, DateOnly endDate, DateOnly today);
}
