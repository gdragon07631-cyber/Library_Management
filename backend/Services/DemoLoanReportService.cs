<<<<<<< HEAD:backend/Services/DemoLoanReportService.cs
=======
using Microsoft.EntityFrameworkCore;
using Project.Data;
>>>>>>> origin/feature/S4-07/xem_10_dau_sach_duoc_muon:Services/DemoLoanReportService.cs
using Project.Models;

namespace Project.Services;

<<<<<<< HEAD:backend/Services/DemoLoanReportService.cs
/// <summary>Deterministic, in-memory sample data for the practice report page.</summary>
public sealed class DemoLoanReportService : ILoanReportService
=======
/// <summary>Practice summary data plus real database queries for the book ranking.</summary>
public sealed class DemoLoanReportService(ApplicationDbContext db) : ILoanReportService
>>>>>>> origin/feature/S4-07/xem_10_dau_sach_duoc_muon:Services/DemoLoanReportService.cs
{
    private sealed record DemoLoan(DateOnly LoanDate, DateOnly DueDate, DateOnly? ReturnedDate);

    public LoanReportViewModel GetDemoReport(DateOnly startDate, DateOnly endDate, DateOnly today)
    {
        var samples = CreateSamples(today);
        var totalLoans = samples.Count(loan => loan.LoanDate >= startDate && loan.LoanDate <= endDate);
        var totalReturns = samples.Count(loan => loan.ReturnedDate is { } returned && returned >= startDate && returned <= endDate);
        var openLoans = samples.Count(loan => loan.ReturnedDate is null);
        var overdueLoans = samples.Count(loan => loan.ReturnedDate is null && loan.DueDate < today);
        var overdueRate = CalculateOverdueRate(overdueLoans, openLoans);

        return new LoanReportViewModel(startDate, endDate, totalLoans, totalReturns, openLoans,
            overdueLoans, overdueRate, totalLoans > 0 || totalReturns > 0);
    }

    public static decimal CalculateOverdueRate(int overdueLoans, int openLoans) =>
        openLoans == 0 ? 0m : (decimal)overdueLoans / openLoans * 100m;

<<<<<<< HEAD:backend/Services/DemoLoanReportService.cs
=======
    public async Task<IReadOnlyList<TopBookReportRow>> GetTopBooksAsync(
        DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
    {
        var loanCounts = db.BookLoans.AsNoTracking()
            .Where(loan => loan.LoanDate >= startDate && loan.LoanDate <= endDate)
            .GroupBy(loan => loan.BookId)
            .Select(group => new { BookId = group.Key, LoanCount = group.Count() });
        var copyCounts = db.BookCopies.AsNoTracking()
            .GroupBy(copy => copy.BookId)
            .Select(group => new { BookId = group.Key, PhysicalCopyCount = group.Count() });

        var rows = await (
            from loanCount in loanCounts
            join book in db.Books.AsNoTracking() on loanCount.BookId equals book.Id
            join copyCount in copyCounts on book.Id equals copyCount.BookId into copies
            from copyCount in copies.DefaultIfEmpty()
            orderby loanCount.LoanCount descending, book.Title ascending, book.Id ascending
            select new
            {
                book.Id,
                book.Title,
                loanCount.LoanCount,
                PhysicalCopyCount = copyCount == null ? 0 : copyCount.PhysicalCopyCount
            })
            .Take(10)
            .ToListAsync(cancellationToken);

        return rows.Select((row, index) => new TopBookReportRow(
            index + 1, row.Id, row.Title, row.LoanCount, row.PhysicalCopyCount)).ToList();
    }

>>>>>>> origin/feature/S4-07/xem_10_dau_sach_duoc_muon:Services/DemoLoanReportService.cs
    private static List<DemoLoan> CreateSamples(DateOnly today)
    {
        var start = today.AddDays(-29);
        return
        [
            new(start, start.AddDays(14), start.AddDays(5)),
            new(today.AddDays(-10), today.AddDays(4), today),
            new(today.AddDays(-8), today.AddDays(-1), null),
            new(today.AddDays(-4), today, null),
            new(today.AddDays(-2), today.AddDays(7), null),
            new(start.AddDays(-4), start.AddDays(14), start),
            new(start.AddDays(-3), today.AddDays(15), null)
        ];
    }
}
