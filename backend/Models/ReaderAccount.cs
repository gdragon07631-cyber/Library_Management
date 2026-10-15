using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Project.Models;

public sealed class ReaderFee
{
    public int Id { get; set; }
    public int ReaderAccountId { get; set; }
    /// <summary>Loan that produced this receivable; null is reserved for other fee types.</summary>
    public long? BookLoanId { get; set; }
    public decimal Amount { get; set; }
    public decimal PaidAmount { get; set; }
    public bool IsPaid { get; set; }
    public string? Status { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public BookLoan? BookLoan { get; set; }

    public decimal RemainingAmount
    {
        get
        {
            if (IsPaid || string.Equals(Status, "Đã thanh toán", StringComparison.OrdinalIgnoreCase) || string.Equals(Status, "Da thanh toan", StringComparison.OrdinalIgnoreCase))
                return 0m;
            var remaining = Amount - PaidAmount;
            return remaining > 0m ? remaining : 0m;
        }
    }
}

public sealed class ReaderAccount
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string FullName { get; set; } = string.Empty;

    [Required]
    public DateOnly DateOfBirth { get; set; }

    [Required, EmailAddress, MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required, MaxLength(20)]
    public string PhoneNumber { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Address { get; set; }

    [Required, MaxLength(50)]
    public string StudentOrStaffCode { get; set; } = string.Empty;

    [Required, MaxLength(512)]
    public string PasswordHash { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string Status { get; set; } = "Chờ duyệt";

    [MaxLength(1000)]
    public string? RejectionReason { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public int SessionVersion { get; set; }

    /// <summary>
    /// Email đã được xác nhận qua liên kết gửi về hộp thư. Tài khoản tự đăng ký mới bắt đầu ở false;
    /// tài khoản có từ trước khi có chức năng này được coi là đã xác nhận.
    /// </summary>
    public bool EmailConfirmed { get; set; } = true;

    /// <summary>Loại thẻ bạn đọc chọn khi tự đăng ký; dùng để cấp thẻ tự động lúc xác nhận email.</summary>
    public int? RequestedLibraryCardTypeId { get; set; }

    /// <summary>Email mới bạn đọc yêu cầu đổi sang; chỉ thay cho <see cref="Email"/> khi được xác nhận qua liên kết.</summary>
    [MaxLength(256)]
    public string? PendingEmail { get; set; }

    /// <summary>Quản trị hệ thống khoá tài khoản: không đăng nhập được, mọi phiên đang mở bị thu hồi.</summary>
    public bool IsLocked { get; set; }

    [MaxLength(500)]
    public string? LockReason { get; set; }

    public decimal OutstandingBalance { get; set; }

    [NotMapped]
    public ICollection<ReaderFee> Fees { get; set; } = [];

    [NotMapped]
    public decimal TotalDebt
    {
        get
        {
            if (Fees != null && Fees.Count > 0)
            {
                return Fees.Sum(f => f.RemainingAmount);
            }
            return OutstandingBalance > 0m ? OutstandingBalance : 0m;
        }
    }

    public LibraryCard? LibraryCard { get; set; }
    public ICollection<BookHold> BookHolds { get; set; } = [];
    public ICollection<ReaderPasswordHistory> PasswordHistories { get; set; } = [];
}
