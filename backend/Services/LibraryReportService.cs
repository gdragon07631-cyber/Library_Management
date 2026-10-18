using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed class LibraryReportService(ApplicationDbContext db) : ILibraryReportService
{
    public async Task<LoanReportSummary> GetSummaryAsync(DateOnly startDate, DateOnly endDate, DateOnly today, CancellationToken cancellationToken = default)
    {
        var inRange = db.BookLoans.Where(loan => loan.LoanDate >= startDate && loan.LoanDate <= endDate);
        var total = await inRange.CountAsync(cancellationToken);
        var overdue = await db.BookLoans.CountAsync(loan => loan.DueDate < today, cancellationToken);
        // BookLoan has no returned/closed state; the existing loan workflow treats every saved loan as open.
        var open = await db.BookLoans.CountAsync(cancellationToken);
        var rate = open == 0 ? 0m : Math.Round((decimal)overdue * 100m / open, 2);
        return new LoanReportSummary(total, 0, open, overdue, rate);
    }

    public async Task<IReadOnlyList<TopBorrowedBook>> GetTopBorrowedBooksAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
    {
        var loanCounts = db.BookLoans.AsNoTracking()
            .Where(loan => loan.LoanDate >= startDate && loan.LoanDate <= endDate)
            .GroupBy(loan => loan.BookId)
            .Select(group => new { BookId = group.Key, LoanCount = group.Count() });
        var copyCounts = db.BookCopies.AsNoTracking()
            .GroupBy(copy => copy.BookId)
            .Select(group => new { BookId = group.Key, CopyCount = group.Count() });

        return await (from count in loanCounts
                      join book in db.Books.AsNoTracking() on count.BookId equals book.Id
                      join copies in copyCounts on book.Id equals copies.BookId into copyGroup
                      from copies in copyGroup.DefaultIfEmpty()
                      orderby count.LoanCount descending, book.Title, book.Id
                      select new TopBorrowedBook(book.Id, book.Title, count.LoanCount, copies == null ? 0 : copies.CopyCount))
            .Take(10).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TopHeldBook>> GetTopHeldBooksAsync(CancellationToken cancellationToken = default) =>
        await db.BookHolds.AsNoTracking()
            .GroupBy(hold => hold.BookId)
            .Select(group => new { BookId = group.Key, HoldCount = group.Count() })
            .Join(db.Books.AsNoTracking(), group => group.BookId, book => book.Id,
                (group, book) => new { book.Id, book.Title, group.HoldCount })
            .OrderByDescending(item => item.HoldCount).ThenBy(item => item.Title).ThenBy(item => item.Id)
            .Take(10)
            .Select(item => new TopHeldBook(item.Id, item.Title, item.HoldCount))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DailyLoanCount>> GetDailyLoansAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
    {
        var grouped = await db.BookLoans.AsNoTracking()
            .Where(loan => loan.LoanDate >= startDate && loan.LoanDate <= endDate)
            .GroupBy(loan => loan.LoanDate)
            .Select(group => new { Date = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Date, item => item.Count, cancellationToken);
        var result = new List<DailyLoanCount>();
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
            result.Add(new DailyLoanCount(date, grouped.GetValueOrDefault(date)));
        return result;
    }
}
