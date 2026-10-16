namespace Project.Services;

public sealed record OverdueFeeCalculation(int OverdueDays, decimal DailyRate, decimal RawFee, decimal EstimatedFee);

/// <summary>Pure calendar-day calculation shared by return preview and confirmation.</summary>
/// <remarks>
/// S4-01 (PO xác nhận 16/10/2026):
/// - Số ngày trễ = số ngày thư viện mở cửa trong khoảng (hạn trả, ngày trả thực tế]; trả đúng/trước hạn = 0.
/// - Phí thô = số ngày trễ × mức phí mỗi ngày (LoanPolicy.DailyOverdueFee do Quản lý khai báo).
/// - Phí dự kiến = phí thô làm tròn đến bội số 1.000 VNĐ gần nhất; đúng mức giữa (x.500) làm tròn lên.
/// </remarks>
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
