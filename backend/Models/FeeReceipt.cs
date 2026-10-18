namespace Project.Models;

/// <summary>Names captured at payment time. Never reconstructed from today's account data.</summary>
public sealed class FeeReceipt
{
    public long FeePaymentId { get; set; }
    public FeePayment? FeePayment { get; set; }
    public string ReaderName { get; set; } = string.Empty;
    public string CollectedByName { get; set; } = string.Empty;
    public ICollection<FeeReceiptLine> Lines { get; set; } = [];
}

public sealed class FeeReceiptLine
{
    public long FeePaymentAllocationId { get; set; }
    public FeePaymentAllocation? FeePaymentAllocation { get; set; }
    public long FeePaymentId { get; set; }
    public FeeReceipt? FeeReceipt { get; set; }
    public string Description { get; set; } = string.Empty;
}
