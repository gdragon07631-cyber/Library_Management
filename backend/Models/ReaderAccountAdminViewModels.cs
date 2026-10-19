using System.ComponentModel.DataAnnotations;

namespace Project.Models;

/// <summary>Quản trị hệ thống sửa thông tin tài khoản bạn đọc để hỗ trợ khi bạn đọc gặp sự cố.</summary>
public sealed class ReaderAccountAdminFormViewModel
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Họ và tên không được để trống.")]
    [MaxLength(100, ErrorMessage = "Họ và tên không được vượt quá 100 ký tự.")]
    [Display(Name = "Họ và tên")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Ngày sinh không được để trống.")]
    [DataType(DataType.Date)]
    [NotFutureDate(ErrorMessage = "Ngày sinh không hợp lệ.")]
    [Display(Name = "Ngày sinh")]
    public DateOnly? DateOfBirth { get; set; }

    [Required(ErrorMessage = "Email không được để trống.")]
    [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
    [MaxLength(256, ErrorMessage = "Email không được vượt quá 256 ký tự.")]
    [Display(Name = "Email đăng nhập")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Số điện thoại không được để trống.")]
    [MaxLength(20, ErrorMessage = "Số điện thoại không được vượt quá 20 ký tự.")]
    [RegularExpression(@"0[0-9]{9}", ErrorMessage = "Số điện thoại không hợp lệ.")]
    [Display(Name = "Số điện thoại")]
    public string PhoneNumber { get; set; } = string.Empty;

    [MaxLength(500, ErrorMessage = "Địa chỉ không được vượt quá 500 ký tự.")]
    [Display(Name = "Địa chỉ")]
    public string? Address { get; set; }

    [Required(ErrorMessage = "Mã sinh viên hoặc mã cán bộ không được để trống.")]
    [MaxLength(50, ErrorMessage = "Mã sinh viên hoặc mã cán bộ không được vượt quá 50 ký tự.")]
    [Display(Name = "Mã sinh viên / mã cán bộ")]
    public string StudentOrStaffCode { get; set; } = string.Empty;
}

/// <summary>Trang chi tiết một tài khoản bạn đọc: thông tin, thẻ, đặt giữ và lịch sử thao tác.</summary>
public sealed class ReaderAccountAdminDetailsViewModel
{
    public required ReaderAccount Reader { get; init; }
    public required ReaderAccountAdminFormViewModel Form { get; init; }
    public IReadOnlyList<BookHold> ActiveHolds { get; init; } = [];
    public IReadOnlyList<AuditLog> History { get; init; } = [];
    public IReadOnlyList<LibraryCardLockHistory> CardLockHistory { get; init; } = [];
}
