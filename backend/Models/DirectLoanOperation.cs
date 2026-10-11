namespace Project.Models;

// Durable request identity. Registration survives rollback; loan effects never do.
public sealed class DirectLoanOperation
{
    public Guid RequestId { get; set; }
    public int StaffId { get; set; }
    public string PayloadHash { get; set; } = "";
    public string CardCode { get; set; } = "";
    public string CopyCodesJson { get; set; } = "[]";
    public string RowIdsJson { get; set; } = "[]";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

public sealed class DirectLoanOperationItem
{
    public Guid RequestId { get; set; }
    public int Position { get; set; }
    public long BookLoanId { get; set; }
    public BookLoan? BookLoan { get; set; }
}
