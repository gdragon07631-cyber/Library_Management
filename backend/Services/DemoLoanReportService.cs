using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

/// <summary>Deterministic, in-memory sample data for the practice report page plus optional database queries.</summary>
public sealed class DemoLoanReportService(ApplicationDbContext? db = null) : ILoanReportService
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

    public LoanReportViewModel GetDemoSummary(DateOnly startDate, DateOnly endDate, DateOnly today) =>
        GetDemoReport(startDate, endDate, today);

    public static decimal CalculateOverdueRate(int overdueLoans, int openLoans) =>
        openLoans == 0 ? 0m : (decimal)overdueLoans / openLoans * 100m;

    public async Task<IReadOnlyList<TopBookReportRow>> GetTopBooksAsync(
        DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
    {
        if (db is null) return [];
        return await new LoanReportService(db).GetTopBooksAsync(startDate, endDate, cancellationToken);
    }

    public async Task<IReadOnlyList<TopHeldBookReportRow>> GetTopHeldBooksAsync(
        CancellationToken cancellationToken = default)
    {
        if (db is null) return [];
        return await new LoanReportService(db).GetTopHeldBooksAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DailyLoanReportRow>> GetDailyLoansAsync(
        DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
    {
        if (db is null) return [];
        return await new LoanReportService(db).GetDailyLoansAsync(startDate, endDate, cancellationToken);
    }

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
