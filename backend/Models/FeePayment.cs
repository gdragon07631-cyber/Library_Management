namespace Project.Models;

public sealed class FeePayment
{
    public long Id { get; set; }
    public Guid RequestId { get; set; }
    public int ReaderAccountId { get; set; }
    public ReaderAccount? ReaderAccount { get; set; }
    public int CollectedByAdminAccountId { get; set; }
    public AdminAccount? CollectedByAdminAccount { get; set; }
    public decimal Amount { get; set; }
    public decimal BalanceAfter { get; set; }
    public DateTime PaidAtUtc { get; set; }
    public ICollection<FeePaymentAllocation> Allocations { get; set; } = [];
}

public sealed class FeePaymentAllocation
{
    public long Id { get; set; }
    public long FeePaymentId { get; set; }
    public FeePayment? FeePayment { get; set; }
    public int ReaderFeeId { get; set; }
    public ReaderFee? ReaderFee { get; set; }
    public decimal Amount { get; set; }
}
