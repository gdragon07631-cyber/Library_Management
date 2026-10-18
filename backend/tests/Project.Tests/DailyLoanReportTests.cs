using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class DailyLoanReportTests
{
    [Fact]
    public async Task ReturnsEveryDateAcrossMonthAndYearWithZeroDaysIncluded()
    {
        await using var db = CreateDb();
        var start = new DateOnly(2026, 12, 30);
        var end = new DateOnly(2027, 1, 2);
        db.BookLoans.AddRange(Loan(start), Loan(start), Loan(new DateOnly(2027, 1, 1)),
            Loan(start.AddDays(-1)), Loan(end.AddDays(1)));
        await db.SaveChangesAsync();

        var result = await new LoanReportService(db).GetDailyLoansAsync(start, end);

        Assert.Equal(new[]
        {
            new DailyLoanReportRow(start, 2),
            new DailyLoanReportRow(new DateOnly(2026, 12, 31), 0),
            new DailyLoanReportRow(new DateOnly(2027, 1, 1), 1),
            new DailyLoanReportRow(end, 0)
        }, result);
    }

    [Fact]
    public async Task SingleDayRangeReturnsExactlyOneDailyValue()
    {
        await using var db = CreateDb();
        var day = new DateOnly(2026, 10, 5);
        db.BookLoans.AddRange(Loan(day), Loan(day.AddDays(-1)));
        await db.SaveChangesAsync();

        var result = await new LoanReportService(db).GetDailyLoansAsync(day, day);

        Assert.Equal(new[] { new DailyLoanReportRow(day, 1) }, result);
    }

    [Fact]
    public async Task NoLoansReturnsContinuousZeroValues()
    {
        await using var db = CreateDb();
        var start = new DateOnly(2026, 5, 1);
        var result = await new LoanReportService(db).GetDailyLoansAsync(start, start.AddDays(2));

        Assert.Equal(new[]
        {
            new DailyLoanReportRow(start, 0),
            new DailyLoanReportRow(start.AddDays(1), 0),
            new DailyLoanReportRow(start.AddDays(2), 0)
        }, result);
    }

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new ApplicationDbContext(options);
    }

    private static BookLoan Loan(DateOnly date) => new()
    {
        BookId = 1, ReaderAccountId = 1, LoanDate = date,
        OriginalDueDate = date.AddDays(14), DueDate = date.AddDays(14)
    };
}
