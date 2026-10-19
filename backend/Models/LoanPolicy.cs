using System.ComponentModel.DataAnnotations;

namespace Project.Models;

/// <summary>Chính sách mượn dùng chung của thư viện (một bản ghi duy nhất).</summary>
public sealed class LoanPolicy
{
    public const int SingletonId = 1;
    public const int DefaultLoanDays = 14;
    public const decimal DefaultDebtLockThreshold = 500000m;

    public int Id { get; set; }

    public int LoanDays { get; set; } = DefaultLoanDays;

    public decimal DebtLockThreshold { get; set; } = DefaultDebtLockThreshold;

    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class LoanPolicyViewModel
{
    [Required(ErrorMessage = "Vui lòng nhập số ngày mượn.")]
    [Range(1, 365, ErrorMessage = "Số ngày mượn phải từ 1 đến 365.")]
    [Display(Name = "Số ngày mượn tối đa")]
    public int LoanDays { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "Ngưỡng nợ phí phải là số không âm.")]
    [Display(Name = "Ngưỡng nợ phí đề xuất khoá thẻ")]
    public decimal DebtLockThreshold { get; set; } = LoanPolicy.DefaultDebtLockThreshold;

    public DateTime? UpdatedAtUtc { get; set; }

    public IReadOnlyList<CardTypeLoanDaysViewModel> CardTypePolicies { get; set; } = [];

    public IReadOnlyList<LockProposalViewModel> LockProposals { get; set; } = [];
}

public sealed class LockProposalViewModel
{
    /// <summary>Quá hạn được tính khi số ngày quá hạn lớn hơn mốc này (31 ngày trở lên).</summary>
    public const int OverdueDaysLimit = 30;
    public const string OverdueReason = "Quá hạn trả sách trên 30 ngày";
    public const string DebtReason = "Nợ phí vượt ngưỡng";

    public int ReaderAccountId { get; set; }
    public string ReaderCode { get; set; } = string.Empty;
    public string ReaderName { get; set; } = string.Empty;
    public string? CardCode { get; set; }
    public int MaxOverdueDays { get; set; }
    public decimal OutstandingDebt { get; set; }
    public decimal DebtThreshold { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsOverdueViolation { get; set; }
    public bool IsDebtViolation { get; set; }
    public IReadOnlyList<string> Reasons { get; set; } = [];

    /// <summary>Các phiếu chưa trả đang quá hạn trên 30 ngày (căn cứ của lý do quá hạn).</summary>
    public IReadOnlyList<LockProposalLoanViewModel> OverdueLoans { get; set; } = [];
}

public sealed class UpdateDebtThresholdRequest
{
    public decimal? DebtLockThreshold { get; set; }
}

public sealed class LockProposalLoanViewModel
{
    public long LoanId { get; set; }
    public string BookTitle { get; set; } = string.Empty;
    public string? CopyCode { get; set; }
    public DateOnly DueDate { get; set; }
    public int OverdueDays { get; set; }
}

public sealed class CardTypeLoanDaysViewModel
{
    public int LibraryCardTypeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? LoanDays { get; set; }
}

public sealed class UpdateCardTypeLoanDaysViewModel
{
    [Range(1, int.MaxValue, ErrorMessage = "Vui lòng chọn loại thẻ.")]
    public int LibraryCardTypeId { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập số ngày mượn cho loại thẻ."), Range(1, 365, ErrorMessage = "Số ngày mượn phải từ 1 đến 365.")]
    public int? LoanDays { get; set; }
}
