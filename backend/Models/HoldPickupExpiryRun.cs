using System.ComponentModel.DataAnnotations;

namespace Project.Models;

public static class HoldPickupExpiryRunStatus
{
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}

/// <summary>One durable daily scheduler record. Failed or abandoned attempts can be safely retried.</summary>
public sealed class HoldPickupExpiryRun
{
    public long Id { get; set; }
    public DateOnly BusinessDate { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    [Required, MaxLength(20)]
    public string Status { get; set; } = HoldPickupExpiryRunStatus.Running;

    public int CheckedCount { get; set; }
    public int CancelledCount { get; set; }
    public int AssignedToNextReaderCount { get; set; }
    public int ReturnedToAvailableCount { get; set; }

    [MaxLength(2000)]
    public string? ErrorMessage { get; set; }

    [MaxLength(500)]
    public string? ResultMessage { get; set; }
}
