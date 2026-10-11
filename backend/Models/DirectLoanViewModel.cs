using System.ComponentModel.DataAnnotations;

namespace Project.Models;

public sealed class DirectLoanRequest
{
    [Required(ErrorMessage = "Vui lòng nhập mã thẻ bạn đọc."), MaxLength(32)]
    public string CardCode { get; set; } = string.Empty;
    [Required(ErrorMessage = "Vui lòng nhập mã vạch bản sao."), MaxLength(50)]
    public string CopyCode { get; set; } = string.Empty;
}

public sealed class DirectLoanViewModel
{
    public string CardCode { get; set; } = string.Empty;
    public string CopyCode { get; set; } = string.Empty;
    public string? ReaderName { get; set; }
    public string? CardTypeName { get; set; }
    public int CurrentLoans { get; set; }
    public int RemainingLoans { get; set; }
    public int MaxBooks { get; set; }
    public string? ReaderError { get; set; }
    public string? BookTitle { get; set; }
    public string? CopyStatus { get; set; }
    public string? CopyError { get; set; }
    public BookLoan? Receipt { get; set; }
    public bool CanConfirm => ReaderName != null && BookTitle != null && ReaderError == null && CopyError == null;
}
