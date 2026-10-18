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

    /// <summary>
    /// Phí trễ hạn theo ngày do Quản lý cấu hình (VNĐ).
    /// <c>null</c> nghĩa là Quản lý chưa khai báo; hệ thống không tự áp mức phí mặc định (PO xác nhận).
    /// </summary>
    public decimal? DailyOverdueFee { get; set; }

    public decimal DebtLockThreshold { get; set; } = DefaultDebtLockThreshold;

    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class LoanPolicyViewModel
{
    [Required(ErrorMessage = "Vui lòng nhập số ngày mượn.")]
    [Range(1, 365, ErrorMessage = "Số ngày mượn phải từ 1 đến 365.")]
    [Display(Name = "Số ngày mượn tối đa")]
    public int LoanDays { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập mức phí trễ hạn mỗi ngày.")]
    [Range(typeof(decimal), "0", "1000000000", ErrorMessage = "Mức phí phải từ 0 đến 1.000.000.000 VNĐ/ngày.")]
    [Display(Name = "Phí trễ hạn mỗi ngày")]
    public decimal? DailyOverdueFee { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "Ngưỡng nợ phí phải là số không âm.")]
    [Display(Name = "Ngưỡng nợ phí đề xuất khoá thẻ")]
    public decimal DebtLockThreshold { get; set; } = LoanPolicy.DefaultDebtLockThreshold;

    public DateTime? UpdatedAtUtc { get; set; }

    public IReadOnlyList<CardTypeLoanDaysViewModel> CardTypePolicies { get; set; } = [];

    public IReadOnlyList<LockProposalViewModel> LockProposals { get; set; } = [];
}

public sealed class LockProposalViewModel
{
    public int ReaderAccountId { get; set; }
    public string ReaderName { get; set; } = string.Empty;
    public string? CardCode { get; set; }
    public int MaxOverdueDays { get; set; }
    public decimal OutstandingDebt { get; set; }
    public decimal DebtThreshold { get; set; }
    public string Status { get; set; } = string.Empty;
    public IReadOnlyList<string> Reasons { get; set; } = [];
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
