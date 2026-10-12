using System.ComponentModel.DataAnnotations;

namespace Project.Models;

public sealed class DirectLoanRequest
{
    public Guid RequestId { get; set; }
    [Required(ErrorMessage = "Vui lòng nhập mã thẻ bạn đọc."), MaxLength(32)]
    public string CardCode { get; set; } = string.Empty;
    [Required(ErrorMessage = "Vui lòng nhập mã vạch bản sao."), MaxLength(50)]
    public string CopyCode { get; set; } = string.Empty;
    [MaxLength(160)]
    public string? BypassReason { get; set; }
    public bool ConfirmBypass { get; set; }
}

public sealed class DirectLoanViewModel
{
    public Guid RequestId { get; set; } = Guid.NewGuid();
    public bool VerifyOnly { get; set; }
    public bool Retrying { get; set; }
    public string CardCode { get; set; } = string.Empty;
    public string CopyCode { get; set; } = string.Empty;
    public string? ReaderName { get; set; }
    public int? ReaderAccountId { get; set; }
    public string? CardTypeName { get; set; }
    public int CurrentLoans { get; set; }
    public int RemainingLoans { get; set; }
    public int MaxBooks { get; set; }
    public decimal OutstandingDebt { get; set; }
    public bool CanOverride { get; set; }
    public bool CanOverrideBlock { get; set; }
    public string? ReaderError { get; set; }
    public string? BookTitle { get; set; }
    public string? CopyStatus { get; set; }
    public string? CopyError { get; set; }
    public long? HoldId { get; set; }
    public BookLoan? Receipt { get; set; }
    public List<DirectLoanSelection> SelectedCopies { get; set; } = [];
    public List<BookLoan> Receipts { get; set; } = [];
    public bool CanConfirm => ReaderName != null && BookTitle != null && ReaderError == null && CopyError == null;
}

public sealed record DirectLoanSelection(string CopyCode, string BookTitle, string Status)
{
    public string RowId { get; init; } = Guid.NewGuid().ToString("N");
    public string? Error { get; init; }
    public long? HoldId { get; init; }
    public bool IsValid => Error == null;
}

public sealed class DirectLoanBatchRequest
{
    public Guid RequestId { get; set; }
    [Required, MaxLength(32)]
    public string CardCode { get; set; } = string.Empty;
    public List<string> CopyCodes { get; set; } = [];
    public List<string> RowIds { get; set; } = [];
    [MaxLength(160)]
    public string? BypassReason { get; set; }
    public bool ConfirmBypass { get; set; }
}

public sealed record DirectLoanBatchOutcome(bool IsSuccess, string? ErrorMessage = null, IReadOnlyList<BookLoan>? Loans = null,
    IReadOnlyList<DirectLoanSelection>? Rows = null, bool IsUncertain = false, bool RequestConflict = false,
    DirectLoanOperation? Operation = null);
