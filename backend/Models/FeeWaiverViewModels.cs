using System.ComponentModel.DataAnnotations;

namespace Project.Models;

public sealed class CreateFeeWaiverRequestViewModel
{
    public int ReaderFeeId { get; set; }

    [Range(typeof(decimal), "0.01", "9999999999999999.99")]
    public decimal RequestedAmount { get; set; }

    [Required, StringLength(1000)]
    public string Reason { get; set; } = string.Empty;
}

public sealed class FeeWaiverDecisionViewModel
{
    [StringLength(1000)]
    public string? ReviewNote { get; set; }
}

public sealed record FeeWaiverReceivableViewModel(
    int Id, string ReaderName, string ReaderCode, long? LoanId, string? BookTitle, string? CopyCode,
    decimal OriginalFeeAmount, decimal PaidAmount, decimal WaivedAmount, decimal FeeRemaining,
    decimal ReaderOutstandingBalance, DateTime CreatedAtUtc);

public sealed record FeeWaiverHistoryViewModel(
    long Id, int ReaderFeeId, long? LoanId, string ReaderName, string ReaderCode, string? BookTitle,
    string? CopyCode, decimal OriginalFeeAmount, decimal FeeBalanceAtRequest,
    decimal ReaderBalanceAtRequest, decimal RequestedAmount, decimal PaidAmount,
    decimal WaivedAmount, decimal FeeRemaining, decimal ReaderOutstandingBalance,
    string Reason, string Status, int RequestedByAdminAccountId, string RequestedBy,
    DateTime RequestedAtUtc, int? ReviewedByAdminAccountId, string? ReviewedBy,
    DateTime? ReviewedAtUtc, string? ReviewNote);

public sealed class FeeWaiverLibrarianIndexViewModel
{
    public IReadOnlyList<FeeWaiverReceivableViewModel> EligibleReceivables { get; init; } = [];
    public IReadOnlyList<FeeWaiverHistoryViewModel> Requests { get; init; } = [];
    public CreateFeeWaiverRequestViewModel NewRequest { get; init; } = new();
}

public sealed class FeeWaiverManagerIndexViewModel
{
    public IReadOnlyList<FeeWaiverHistoryViewModel> PendingRequests { get; init; } = [];
}
