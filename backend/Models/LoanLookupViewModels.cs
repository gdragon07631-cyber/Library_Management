using System.Globalization;

namespace Project.Models;

/// <summary>
/// Trạng thái hiển thị của phiếu mượn trên màn hình tra nhanh. BookLoan chưa có cột trạng thái: phiếu còn bản ghi
/// là phiếu đang mượn (trả sách hiện là xóa bản ghi), nên mọi kết quả đều thuộc nhóm "đang mở".
/// </summary>
public static class LoanLookupStatus
{
    public const string Open = "Đang mượn";
    public const string Returned = "Đã trả";

    /// <summary>Các trạng thái thực tế có thể lọc; không tạo trạng thái mới.</summary>
    public static readonly IReadOnlyList<string> All = [Open, Returned];
}

/// <summary>Bộ lọc tùy chọn của tra nhanh phiếu mượn. Null nghĩa là không lọc theo tiêu chí đó.</summary>
public sealed record LoanLookupFilter(DateOnly? From = null, DateOnly? To = null, string? Status = null)
{
    public const string DateFormat = "yyyy-MM-dd";
    public const string ReversedRangeMessage = "Từ ngày không được lớn hơn đến ngày.";
    public const string InvalidDateMessage = "Ngày lọc không hợp lệ.";
    public const string InvalidStatusMessage = "Trạng thái lọc không hợp lệ.";

    public static readonly LoanLookupFilter None = new();

    public bool IsEmpty => From is null && To is null && Status is null;

    /// <summary>Trả thông báo lỗi nếu bộ lọc không hợp lệ, ngược lại null.</summary>
    public string? Validate()
    {
        if (From is not null && To is not null && From > To) return ReversedRangeMessage;
        if (Status is not null && !LoanLookupStatus.All.Contains(Status)) return InvalidStatusMessage;
        return null;
    }

    /// <summary>Đọc bộ lọc từ chuỗi nhập (ngày dạng yyyy-MM-dd của ô date; trạng thái rỗng = tất cả) và kiểm tra.</summary>
    public static LoanLookupFilter? TryParse(string? from, string? to, string? status, out string? error)
    {
        if (!TryParseDate(from, out var fromDate) || !TryParseDate(to, out var toDate))
        {
            error = InvalidDateMessage;
            return null;
        }
        var filter = new LoanLookupFilter(fromDate, toDate, string.IsNullOrWhiteSpace(status) ? null : status.Trim());
        error = filter.Validate();
        return error is null ? filter : null;
    }

    public string? FromText => From?.ToString(DateFormat, CultureInfo.InvariantCulture);
    public string? ToText => To?.ToString(DateFormat, CultureInfo.InvariantCulture);

    private static bool TryParseDate(string? text, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!DateOnly.TryParseExact(text.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;
        date = parsed;
        return true;
    }
}

/// <summary>Nguyên nhân một lần tra cứu hợp lệ không có phiếu nào; chỉ có giá trị khi TotalItems = 0.</summary>
public enum LoanLookupEmptyReason
{
    /// <summary>Không phiếu nào khớp mã, kể cả khi bỏ bộ lọc ngày và trạng thái.</summary>
    NoMatchingCode,
    /// <summary>Mã có phiếu khi bỏ bộ lọc, nhưng khoảng ngày/trạng thái đang chọn loại hết.</summary>
    FilteredOut
}

public static class LoanLookupEmptyReasonCodes
{
    public const string NoMatchingCode = "NO_MATCHING_CODE";
    public const string FilteredOut = "FILTERED_OUT";

    public static string? ToApiValue(LoanLookupEmptyReason? reason) => reason switch
    {
        LoanLookupEmptyReason.NoMatchingCode => NoMatchingCode,
        LoanLookupEmptyReason.FilteredOut => FilteredOut,
        _ => null
    };
}

public sealed record LoanLookupItem(
    long LoanId, string? CardCode, string? CopyCode, string ReaderName, DateOnly LoanDate, DateOnly DueDate, string Status);

public sealed record LoanLookupPage(
    string Code, IReadOnlyList<LoanLookupItem> Items, int Page, int PageSize, int TotalItems)
{
    public int TotalPages => TotalItems == 0 ? 0 : (int)Math.Ceiling(TotalItems / (double)PageSize);
    /// <summary>Bộ lọc đã áp dụng cho trang kết quả này; phân trang dùng lại đúng bộ lọc này.</summary>
    public LoanLookupFilter Filter { get; init; } = LoanLookupFilter.None;
    /// <summary>Vì sao không có kết quả; null khi có kết quả (hoặc dịch vụ tra cứu không xác định nguyên nhân).</summary>
    public LoanLookupEmptyReason? EmptyReason { get; init; }
}

public sealed class LoanLookupViewModel
{
    public string? Code { get; set; }
    /// <summary>Có kết quả tra cứu (kể cả 0 phiếu); null khi chưa tìm, ô rỗng hoặc gặp lỗi.</summary>
    public LoanLookupPage? Result { get; set; }
    public string? ValidationMessage { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Giá trị đang hiển thị trong các ô lọc (giữ nguyên dữ liệu người dùng nhập khi báo lỗi).</summary>
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Status { get; set; }
    public string? FilterError { get; set; }

    /// <summary>Bộ lọc đang áp dụng; nút Tìm kiếm gửi kèm các giá trị này để tìm mã mới mà vẫn giữ bộ lọc.</summary>
    public string? AppliedFrom { get; set; }
    public string? AppliedTo { get; set; }
    public string? AppliedStatus { get; set; }
}
