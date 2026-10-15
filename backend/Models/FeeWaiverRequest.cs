using System.ComponentModel.DataAnnotations;

namespace Project.Models;

public static class FeeWaiverRequestStatus
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}

/// <summary>Immutable proposal and decision history for a partial or full receivable waiver.</summary>
public sealed class FeeWaiverRequest
{
    public long Id { get; set; }
    public int ReaderFeeId { get; set; }
    public decimal OriginalFeeAmount { get; set; }
    public decimal FeeBalanceAtRequest { get; set; }
    public decimal ReaderBalanceAtRequest { get; set; }
    public decimal RequestedAmount { get; set; }
    [Required, MaxLength(1000)]
    public string Reason { get; set; } = string.Empty;
    [Required, MaxLength(20)]
    public string Status { get; set; } = FeeWaiverRequestStatus.Pending;
    public int RequestedByAdminAccountId { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public int? ReviewedByAdminAccountId { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    [MaxLength(1000)]
    public string? ReviewNote { get; set; }

    public ReaderFee? ReaderFee { get; set; }
    public AdminAccount? RequestedByAdminAccount { get; set; }
    public AdminAccount? ReviewedByAdminAccount { get; set; }
}
