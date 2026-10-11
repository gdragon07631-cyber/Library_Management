using System.ComponentModel.DataAnnotations;

namespace Project.Models;

public static class LoanBlockHistoryEventTypes
{
    public const string All = "";
    public const string BlockedLimit = "BlockedLimit";
    public const string BlockedCardExpired = "BlockedCardExpired";
    public const string BlockedCardLocked = "BlockedCardLocked";
    public const string BlockedOverdue = "BlockedOverdue";
    public const string BlockedDebt = "BlockedDebt";
    public const string BlockedMultiple = "BlockedMultiple";
    public const string BlockedOther = "BlockedOther";
    public const string OverrideUsed = "OverrideUsed";
    public const string OverrideNotRequired = "OverrideNotRequired";
    public const string OverrideFailed = "OverrideFailed";

    public static IReadOnlyList<(string Value, string Label)> Options { get; } =
    [
        (All, "Tất cả sự kiện"),
        (BlockedLimit, "Bị chặn do đạt hạn mức sách"),
        (BlockedCardExpired, "Bị chặn do thẻ hết hạn"),
        (BlockedCardLocked, "Bị chặn do thẻ bị khóa"),
        (BlockedOverdue, "Bị chặn do sách quá hạn chưa trả"),
        (BlockedDebt, "Bị chặn do còn nợ phí"),
        (BlockedMultiple, "Bị chặn do nhiều điều kiện cùng lúc"),
        (BlockedOther, "Bị chặn do điều kiện khác"),
        (OverrideUsed, "Bỏ qua chặn thành công"),
        (OverrideNotRequired, "Ngoại lệ không cần thiết"),
        (OverrideFailed, "Bỏ qua chặn thất bại")
    ];

    public static string Label(string eventType) =>
        Options.FirstOrDefault(option => option.Value == eventType).Label ?? "Sự kiện không xác định";

    public static string OutcomeLabel(string outcome) => outcome switch
    {
        "Blocked" => "Đã chặn",
        "Used" => "Đã sử dụng ngoại lệ",
        "NotRequired" => "Đã tạo phiếu; ngoại lệ không cần thiết tại thời điểm xác nhận",
        "Failed" => "Thao tác ngoại lệ thất bại",
        _ => "Không xác định"
    };

    public static string FromBlockingCodes(IEnumerable<string> codes)
    {
        var set = codes.ToHashSet(StringComparer.Ordinal);
        if (set.Count > 1) return BlockedMultiple;
        if (set.Contains("BORROWING_LIMIT_REACHED")) return BlockedLimit;
        if (set.Contains("CARD_EXPIRED")) return BlockedCardExpired;
        if (set.Contains("CARD_LOCKED")) return BlockedCardLocked;
        if (set.Contains("OVERDUE_UNRETURNED_LOANS")) return BlockedOverdue;
        if (set.Contains("UNPAID_FEE")) return BlockedDebt;
        return BlockedOther;
    }
}

public sealed class LoanBlockHistoryFilter
{
    [DataType(DataType.Date)]
    public DateOnly? FromDate { get; set; }

    [DataType(DataType.Date)]
    public DateOnly? ToDate { get; set; }

    [MaxLength(100)]
    public string? ReaderQuery { get; set; }

    [MaxLength(40)]
    public string? EventType { get; set; }

    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;

    [Range(10, 100)]
    public int PageSize { get; set; } = 25;
}

public sealed record LoanBlockHistoryPage(
    IReadOnlyList<AuditBlockLog> Records, int TotalCount, int Page, int PageSize)
{
    public int PageCount => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}

public sealed class LoanBlockHistoryIndexViewModel
{
    public LoanBlockHistoryFilter Filter { get; set; } = new();
    public IReadOnlyList<AuditBlockLog> Records { get; set; } = [];
    public IReadOnlyList<(string Value, string Label)> EventTypes { get; set; } = LoanBlockHistoryEventTypes.Options;
    public int TotalCount { get; set; }
    public int PageCount { get; set; } = 1;
    public string? ErrorMessage { get; set; }
    public AuditBlockLog? SelectedRecord { get; set; }
}
