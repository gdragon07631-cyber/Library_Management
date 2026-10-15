using System.Globalization;

namespace Project.Models;

/// <summary>Blank means no threshold; zero is an explicitly applied threshold.</summary>
public static class OverdueReaderExportFilter
{
    public const string ValidationMessage = "Số ngày trễ tối thiểu phải là số nguyên không âm từ 0 đến 2147483647";
    public const string CardTypeValidationMessage = "Loại thẻ không hợp lệ. Vui lòng chọn một loại thẻ đang hoạt động hoặc Tất cả.";

    public static bool TryParseCardType(string? value, out int? cardTypeId) =>
        TryParse(value, out cardTypeId) && (cardTypeId is null || cardTypeId > 0);

    public static bool TryParse(string? value, out int? minimumDays)
    {
        minimumDays = null;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return true;
        if (text.Any(c => c < '0' || c > '9') ||
            !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return false;
        minimumDays = parsed;
        return true;
    }
}
