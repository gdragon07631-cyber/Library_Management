using Project.Models;

namespace Project.Services;

public enum ReaderAccountAdminStatus
{
    Success,
    NotFound,
    DuplicateEmail,
    DuplicateCode,
    MissingReason,
    NoChange
}

/// <summary>Kết quả thao tác của quản trị; <see cref="Changes"/> mô tả các trường đã đổi để ghi nhật ký.</summary>
public sealed record ReaderAccountAdminResult(
    ReaderAccountAdminStatus Status,
    string? ErrorMessage = null,
    ReaderAccount? Reader = null,
    IReadOnlyList<string>? Changes = null)
{
    public bool IsSuccess => Status == ReaderAccountAdminStatus.Success;
}

public interface IReaderAccountAdminService
{
    /// <summary>Tìm bạn đọc theo tên, email, số điện thoại, mã SV/CB hoặc mã thẻ; lọc theo trạng thái.</summary>
    Task<IReadOnlyList<ReaderAccount>> SearchAsync(string? keyword, string? status, int limit, CancellationToken cancellationToken = default);

    Task<ReaderAccountAdminDetailsViewModel?> GetDetailsAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Sửa thông tin bạn đọc. Email do quản trị đặt được coi là đã xác nhận và huỷ yêu cầu đổi email đang chờ.</summary>
    Task<ReaderAccountAdminResult> UpdateAsync(int id, ReaderAccountAdminFormViewModel model, CancellationToken cancellationToken = default);

    /// <summary>Khoá (bắt buộc có lý do, thu hồi mọi phiên đăng nhập) hoặc mở khoá tài khoản bạn đọc.</summary>
    Task<ReaderAccountAdminResult> SetLockedAsync(int id, bool locked, string? reason, CancellationToken cancellationToken = default);

    /// <summary>Khoá thẻ bạn đọc theo thời hạn hoặc cho đến khi mở khoá thủ công.</summary>
    Task<ReaderAccountAdminResult> SetCardLockedAsync(
        int readerId,
        string? reason,
        DateTime? lockExpiresAtUtc,
        bool manualUnlock,
        int lockedByAdminAccountId,
        CancellationToken cancellationToken = default);

    /// <summary>Mở khoá thẻ bạn đọc và lưu nhật ký mở khoá cùng với cập nhật trạng thái thẻ.</summary>
    Task<ReaderAccountAdminResult> SetCardUnlockedAsync(
        int readerId,
        string? reason,
        int unlockedByAdminAccountId,
        string actorEmail,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    /// <summary>Đăng xuất bạn đọc khỏi mọi thiết bị (ví dụ khi nghi bị lộ mật khẩu).</summary>
    Task<ReaderAccountAdminResult> RevokeSessionsAsync(int id, CancellationToken cancellationToken = default);
}
