namespace Project.Models;

public static class BookHoldStatus
{
    public const string Waiting = "Đang chờ";
    public const string Pending = Waiting;
    public const string Available = "Đã có sách";
    public const string Assigned = Available;
    public const string Cancelled = "Đã hủy";
    public const string ConvertedToLoan = "Đã chuyển thành phiếu mượn";
    public const string ExpiredCancellationReason = "Quá hạn nhận sách – hệ thống tự hủy";

    public static readonly string[] WaitingPickupStatuses =
    [
        Available,
        "Chờ nhận",
        "Sẵn sàng nhận",
        "WaitingPickup"
    ];

    public static readonly string[] ActiveStatuses = [Waiting, .. WaitingPickupStatuses];
}

public static class BookHoldExpiryCopyOutcome
{
    public const string TransferredToNextReader = "TransferredToNextReader";
    public const string ReturnedToAvailable = "ReturnedToAvailable";
    public const string NoCopyAttached = "NoCopyAttached";
    public const string Unknown = "Unknown";
}

public sealed class BookHold
{
    public long Id { get; set; }
    public int ReaderAccountId { get; set; }
    public ReaderAccount? ReaderAccount { get; set; }
    public int BookId { get; set; }
    public Book? Book { get; set; }
    public DateTime HeldAtUtc { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = BookHoldStatus.Waiting;
    /// <summary>Hạn cuối nhận sách (UTC); chỉ có khi đơn ở trạng thái "Đã có sách".</summary>
    public DateTime? PickupDeadlineUtc { get; set; }
    /// <summary>Bản sao đang được giữ cho đơn này (nếu có).</summary>
    public long? BookCopyId { get; set; }
    public BookCopy? BookCopy { get; set; }
    [System.ComponentModel.DataAnnotations.MaxLength(1000)]
    public string? CancellationReason { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public int? CancelledByAdminAccountId { get; set; }

    /// <summary>Copy snapshot and outcome captured when the system expires this hold.</summary>
    public long? ExpiredBookCopyId { get; set; }
    [System.ComponentModel.DataAnnotations.MaxLength(50)]
    public string? ExpiredBookCopyCode { get; set; }
    [System.ComponentModel.DataAnnotations.MaxLength(30)]
    public string? ExpiryCopyOutcome { get; set; }
    public long? ExpiryNextHoldId { get; set; }
}
