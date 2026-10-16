using System.ComponentModel.DataAnnotations;

namespace Project.Models;

public static class AuditActions
{
    public const string Login = "Đăng nhập";
    public const string CreateAccount = "Tạo tài khoản";
    public const string UpdateAccount = "Sửa tài khoản";
    public const string IssueCard = "Cấp thẻ";
    public const string UpdateLoanPolicy = "Sửa chính sách mượn";
    public const string LoginFailed = "Đăng nhập thất bại";
    public const string Logout = "Đăng xuất";
    public const string ChangePassword = "Đổi mật khẩu";
    public const string ResetPassword = "Đặt lại mật khẩu";
    public const string CreateLoan = "Mượn sách";
    public const string RenewLoan = "Gia hạn mượn";
    public const string PlaceHold = "Đặt giữ sách";
    public const string CancelHold = "Hủy đặt giữ";
    public const string BlockLoan = "Chặn mượn sách";
    public const string OverrideBlockLoan = "Bỏ qua chặn mượn sách";
    public const string LockCard = "Khóa thẻ";
    public const string UnlockCard = "Mở khóa thẻ";

    public static readonly IReadOnlyList<string> All =
    [
        Login, LoginFailed, Logout, CreateAccount, UpdateAccount, ChangePassword, ResetPassword,
        IssueCard, LockCard, UnlockCard, CreateLoan, RenewLoan, PlaceHold, CancelHold, UpdateLoanPolicy
    ];

    /// <summary>Màu nhãn trên trang nhật ký để phân biệt nhanh từng nhóm hành động.</summary>
    public static string BadgeClass(string action) => action switch
    {
        LoginFailed or BlockLoan => "bg-danger-subtle text-danger-emphasis",
        OverrideBlockLoan => "bg-warning-subtle text-warning-emphasis",
        Login or Logout => "bg-secondary-subtle text-secondary-emphasis",
        ChangePassword or ResetPassword => "bg-warning-subtle text-warning-emphasis",
        LockCard or BlockLoan => "bg-danger-subtle text-danger-emphasis",
        UnlockCard => "bg-success-subtle text-success-emphasis",
        CreateLoan or RenewLoan => "bg-success-subtle text-success-emphasis",
        PlaceHold or CancelHold => "bg-info-subtle text-info-emphasis",
        _ => "bg-primary-subtle text-primary-emphasis"
    };
}

/// <summary>Nhật ký hoạt động: ai đã thao tác gì, lúc nào, trên đối tượng nào và từ IP nào.</summary>
public sealed class AuditLog
{
    public long Id { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    [Required, MaxLength(256)]
    public string Actor { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string Action { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string Target { get; set; } = string.Empty;

    [Required, MaxLength(45)]
    public string IpAddress { get; set; } = string.Empty;
}
