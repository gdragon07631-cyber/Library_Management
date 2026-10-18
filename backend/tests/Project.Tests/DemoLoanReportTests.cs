using Project.Services;

namespace Project.Tests;

public sealed class DemoLoanReportTests
{
    private static readonly DateOnly Today = new(2026, 10, 17);

    [Fact]
    public void DefaultThirtyDayWindowCountsLoanAndReturnBoundariesInclusively()
    {
        var start = Today.AddDays(-29);
        var report = new DemoLoanReportService().GetDemoReport(start, Today, Today);

        Assert.Equal(5, report.TotalLoans);
        Assert.Equal(3, report.TotalReturns);
        Assert.True(report.HasActivityInRange);
    }

    [Fact]
    public void CurrentOpenAndOverdueCountsDoNotDependOnSelectedDateRange()
    {
        var report = new DemoLoanReportService().GetDemoReport(Today, Today, Today);

        Assert.Equal(4, report.OpenLoans);
        Assert.Equal(1, report.OverdueLoans);
        Assert.Equal(25m, report.OverdueRate);
    }

    [Fact]
    public void RangeWithoutLoanOrReturnEventsReturnsZeroEventsAndKeepsCurrentSnapshot()
    {
        var report = new DemoLoanReportService().GetDemoReport(
            new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 31), Today);

        Assert.Equal(0, report.TotalLoans);
        Assert.Equal(0, report.TotalReturns);
        Assert.False(report.HasActivityInRange);
        Assert.Equal(4, report.OpenLoans);
        Assert.Equal(1, report.OverdueLoans);
    }

    [Fact]
    public void OverdueRateIsZeroWhenThereAreNoOpenLoans()
    {
        Assert.Equal(0m, DemoLoanReportService.CalculateOverdueRate(0, 0));
    }
}
