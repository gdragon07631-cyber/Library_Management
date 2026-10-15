using System.ComponentModel.DataAnnotations;

namespace Project.Models;

public static class EmailSendStatus
{
    public const string Sending = "Sending";
    public const string Retrying = "Retrying";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
}

public sealed class EmailSendHistory
{
    public long Id { get; set; }
    [Required, MaxLength(64)] public string DeduplicationKey { get; set; } = string.Empty;
    public int? ReaderAccountId { get; set; }
    [Required, MaxLength(256)] public string Recipient { get; set; } = string.Empty;
    [Required, MaxLength(30)] public string NotificationType { get; set; } = "Other";
    [Required, MaxLength(500)] public string Subject { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    [Required, MaxLength(20)] public string Status { get; set; } = EmailSendStatus.Sending;
    public int Attempts { get; set; }
    [MaxLength(4000)] public string? AttemptErrors { get; set; }
    [MaxLength(1000)] public string? ErrorMessage { get; set; }
    public int? OverdueMilestoneDays { get; set; }
}

public sealed record EmailSendHistoryItem(long Id, DateTime StartedAtUtc, DateTime? CompletedAtUtc,
    int? ReaderAccountId, string? ReaderCode, string Recipient, string NotificationType,
    string Subject, string Status, int Attempts, string? ErrorMessage, int? OverdueMilestoneDays);

public sealed class EmailSendHistoryPageViewModel
{
    public IReadOnlyList<EmailSendHistoryItem> Items { get; init; } = [];
    public string? ReaderCodeFilter { get; init; }
    public string? Email { get; init; }
    public int Page { get; init; }
    public int TotalPages { get; init; }
}
