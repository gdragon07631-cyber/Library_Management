using System.ComponentModel.DataAnnotations;

namespace Project.Models;

public sealed class OverdueReminderLog
{
    public long Id { get; set; }
    public long BookLoanId { get; set; }
    public int MilestoneDays { get; set; }
    public int ReaderAccountId { get; set; }
    public DateOnly BusinessDate { get; set; }
    public DateTime SentAtUtc { get; set; }
    [Required, MaxLength(20)] public string Status { get; set; } = "Sent";
    [MaxLength(500)] public string? ErrorMessage { get; set; }
}
