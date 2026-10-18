using System.ComponentModel.DataAnnotations;

namespace Project.Models;

public sealed class CollectFeeViewModel
{
    public int ReaderAccountId { get; set; }
    public Guid RequestId { get; set; }
    [Required(ErrorMessage = "Vui lòng nhập số tiền.")]
    public decimal? Amount { get; set; }
    public bool Confirmed { get; set; }
}

public sealed record FeePaymentReader(int Id, string FullName, string Code, string Email, string? CardCode);
public sealed record FeePaymentDebt(FeePaymentReader Reader, decimal Balance, IReadOnlyList<ReaderFee> Fees)
{
    public string? CardStatus { get; init; }
    public decimal FeeTotal => Fees.Sum(f => f.RemainingAmount);
    public bool IsConsistent => Balance >= 0m && Balance == FeeTotal;
}

public sealed class FeePaymentIndexViewModel
{
    public string? Search { get; set; }
    public IReadOnlyList<FeePaymentReader> Readers { get; set; } = [];
    public FeePaymentDebt? Debt { get; set; }
    public CollectFeeViewModel Payment { get; set; } = new();
    public IReadOnlyList<FeePaymentHistoryItem> History { get; set; } = [];
}

public sealed record FeePaymentResult(bool IsSuccess, string Message, long? PaymentId = null, decimal? BalanceAfter = null);

public sealed record FeePaymentHistoryItem(long Id, decimal Amount, decimal BalanceAfter, DateTime PaidAtUtc,
    string CollectedBy, IReadOnlyList<FeePaymentHistoryAllocation> Allocations);
public sealed record FeePaymentHistoryAllocation(int ReaderFeeId, decimal Amount);
