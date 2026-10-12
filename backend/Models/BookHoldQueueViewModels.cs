namespace Project.Models;

/// <summary>Query values for the librarian's single-select hold-status filter.</summary>
public static class BookHoldQueueFilter
{
    public const string All = "all";
    public const string Queued = "queued";
    public const string WaitingPickup = "waitingPickup";
    public const string ConvertedToLoan = "convertedToLoan";
    public const string Cancelled = "cancelled";

    public static bool TryNormalize(string? value, out string filter)
    {
        filter = string.IsNullOrWhiteSpace(value) ? All : value.Trim();
        if (string.Equals(filter, All, StringComparison.OrdinalIgnoreCase)) { filter = All; return true; }
        if (string.Equals(filter, Queued, StringComparison.OrdinalIgnoreCase)) { filter = Queued; return true; }
        if (string.Equals(filter, WaitingPickup, StringComparison.OrdinalIgnoreCase)) { filter = WaitingPickup; return true; }
        if (string.Equals(filter, ConvertedToLoan, StringComparison.OrdinalIgnoreCase)) { filter = ConvertedToLoan; return true; }
        if (string.Equals(filter, Cancelled, StringComparison.OrdinalIgnoreCase)) { filter = Cancelled; return true; }
        return false;
    }

    public static string Label(string filter) => filter switch
    {
        Queued => "Đang xếp hàng",
        WaitingPickup => "Đang chờ nhận",
        ConvertedToLoan => "Đã chuyển thành phiếu mượn",
        Cancelled => "Đã hủy",
        _ => "Tất cả"
    };
}

/// <summary>Read-only entry in a title's hold queue/history list.</summary>
public sealed class BookHoldQueueItemViewModel
{
    public long HoldId { get; set; }
    public long? LoanId { get; set; }
    public int Position { get; set; }
    public int ReaderAccountId { get; set; }
    public string ReaderName { get; set; } = string.Empty;
    public DateTime HeldAtUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public long? BookCopyId { get; set; }
    public string? CopyBarcode { get; set; }
    public DateTime? PickupDeadlineUtc { get; set; }
    public string? CancellationReason { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public bool CanBeCancelledByStaff { get; set; }

    public bool IsPickupExpired =>
        (PickupDeadlineUtc is { } deadline && deadline < DateTime.UtcNow) ||
        (Status == BookHoldStatus.Cancelled && CancellationReason == BookHoldStatus.ExpiredCancellationReason);
    public string DisplayStatus => IsPickupExpired ? "Quá hạn nhận" : Status;

    public string HeldAtText => Project.Services.HoldPickupExpirySchedule.GetVietnamLocalTime(HeldAtUtc).ToString("dd/MM/yyyy HH:mm");
    public string? PickupDeadlineText => PickupDeadlineUtc is { } deadline
        ? Project.Services.HoldPickupExpirySchedule.GetVietnamLocalTime(deadline).ToString("dd/MM/yyyy HH:mm")
        : null;
    public string? CancelledAtText => CancelledAtUtc is { } cancelledAt
        ? Project.Services.HoldPickupExpirySchedule.GetVietnamLocalTime(cancelledAt).ToString("dd/MM/yyyy HH:mm")
        : null;
}

public sealed class CancelHoldRequest
{
    public string? Reason { get; set; }
}
