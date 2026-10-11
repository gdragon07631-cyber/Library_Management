namespace Project.Models;

public static class BookHoldStatuses
{
    public const string Pending = "Đang chờ";
    public const string Assigned = "Đã phân bổ";
    public const string Cancelled = "Đã hủy";
}

public sealed class BookHold
{
    public long Id { get; set; }
    public int ReaderAccountId { get; set; }
    public ReaderAccount? ReaderAccount { get; set; }
    public int BookId { get; set; }
    public Book? Book { get; set; }
    public DateTime HeldAtUtc { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = BookHoldStatuses.Pending;
    public long? BookCopyId { get; set; }
    public BookCopy? BookCopy { get; set; }
}
