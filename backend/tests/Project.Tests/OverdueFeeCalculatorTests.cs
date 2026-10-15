using Project.Services;

namespace Project.Tests;

public sealed class OverdueFeeCalculatorTests
{
    private static readonly DateOnly DueDate = new(2026, 10, 10);

    [Theory]
    [InlineData(2026, 10, 9, 0, 0)]
    [InlineData(2026, 10, 10, 0, 0)]
    [InlineData(2026, 10, 11, 1, 2_000)]
    [InlineData(2026, 10, 15, 5, 10_000)]
    [InlineData(2026, 10, 15, 5, 0, 0)]
    public void Calculate_UsesCalendarDaysAndPolicyRate(int year, int month, int day, int expectedDays, decimal expectedFee,
        decimal dailyRate = 2_000m)
    {
        var returnDate = new DateOnly(year, month, day);
        var result = OverdueFeeCalculator.Calculate(DueDate, returnDate, dailyRate, OpenEveryDateAfter(DueDate, returnDate));
        Assert.Equal(expectedDays, result.OverdueDays);
        Assert.Equal(expectedFee, result.EstimatedFee);
    }

    [Theory]
    [InlineData(2_499, 2_000)]
    [InlineData(2_500, 3_000)]
    [InlineData(2_501, 3_000)]
    [InlineData(3_499, 3_000)]
    [InlineData(3_500, 4_000)]
    public void Calculate_RoundsToNearestThousandAndMidpointsUp(decimal rawFee, decimal expectedFee)
    {
        var result = OverdueFeeCalculator.Calculate(DueDate, DueDate.AddDays(1), rawFee, new HashSet<DateOnly> { DueDate.AddDays(1) });
        Assert.Equal(rawFee, result.RawFee);
        Assert.Equal(expectedFee, result.EstimatedFee);
    }

    [Fact]
    public void Calculate_RejectsNegativeDailyRate() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => OverdueFeeCalculator.Calculate(DueDate, DueDate, -1, new HashSet<DateOnly>()));

    private static IReadOnlySet<DateOnly> OpenEveryDateAfter(DateOnly dueDate, DateOnly returnDate)
    {
        var dates = new HashSet<DateOnly>();
        for (var date = dueDate.AddDays(1); date <= returnDate; date = date.AddDays(1)) dates.Add(date);
        return dates;
    }
}
