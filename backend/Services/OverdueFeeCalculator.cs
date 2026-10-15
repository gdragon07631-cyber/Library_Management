namespace Project.Services;

public sealed record OverdueFeeCalculation(int OverdueDays, decimal DailyRate, decimal RawFee, decimal EstimatedFee);

/// <summary>Pure calendar-day calculation shared by return preview and confirmation.</summary>
public static class OverdueFeeCalculator
{
    public const decimal RoundingUnit = 1_000m;

    public static OverdueFeeCalculation Calculate(
        DateOnly dueDate, DateOnly returnDate, decimal dailyRate, IReadOnlySet<DateOnly> openDates)
    {
        if (dailyRate < 0) throw new ArgumentOutOfRangeException(nameof(dailyRate));
        ArgumentNullException.ThrowIfNull(openDates);
        var overdueDays = returnDate > dueDate
            ? openDates.Count(date => date > dueDate && date <= returnDate)
            : 0;
        var rawFee = overdueDays * dailyRate;
        var estimatedFee = Math.Round(rawFee / RoundingUnit, 0, MidpointRounding.AwayFromZero) * RoundingUnit;
        return new(overdueDays, dailyRate, rawFee, estimatedFee);
    }
}
