using System.ComponentModel.DataAnnotations;

namespace Project.Models;

/// <summary>
/// Nhật ký khoá/mở khoá thẻ bạn đọc (S4-04 lát 4). Chỉ thêm mới: database từ chối sửa/xoá (trigger trên SQL Server).
/// Lưu kèm bản sao mã thẻ, tên bạn đọc và người thực hiện để nhật ký vẫn đọc được khi dữ liệu gốc thay đổi.
/// </summary>
public sealed class LibraryCardLockHistory
{
    public long Id { get; set; }

    public int LibraryCardId { get; set; }

    [Required, MaxLength(32)]
    public string CardCode { get; set; } = string.Empty;

    public int ReaderAccountId { get; set; }

    [Required, MaxLength(100)]
    public string ReaderName { get; set; } = string.Empty;

    /// <summary><see cref="LibraryCardLockEvents.Lock"/> hoặc <see cref="LibraryCardLockEvents.Unlock"/>.</summary>
    [Required, MaxLength(20)]
    public string EventType { get; set; } = string.Empty;

    public DateTime OccurredAtUtc { get; set; }

    public int? PerformedByAdminAccountId { get; set; }

    [Required, MaxLength(256)]
    public string PerformedBy { get; set; } = string.Empty;

    /// <summary>Lý do khoá: của lần khoá này (sự kiện khoá) hoặc của lần khoá vừa được mở (sự kiện mở khoá).</summary>
    [MaxLength(1000)]
    public string? LockReason { get; set; }

    [MaxLength(1000)]
    public string? UnlockReason { get; set; }

    /// <summary>Thời điểm bắt đầu và hạn của lần khoá liên quan.</summary>
    public DateTime? LockedAtUtc { get; set; }

    public DateTime? LockExpiresAtUtc { get; set; }

    public bool IsLockUntilManualUnlock { get; set; }
}

public static class LibraryCardLockEvents
{
    public const string Lock = "Khoá thẻ";
    public const string Unlock = "Mở khoá thẻ";

    public static readonly IReadOnlyList<string> All = [Lock, Unlock];
}

public sealed class LibraryCardLockHistoryFilter
{
    public string? Q { get; set; }
    public DateOnly? FromDate { get; set; }
    public DateOnly? ToDate { get; set; }
    public string? EventType { get; set; }
    public int? ReaderAccountId { get; set; }
}

public sealed class LibraryCardLockHistoryPageViewModel
{
    public LibraryCardLockHistoryFilter Filter { get; init; } = new();
    public IReadOnlyList<LibraryCardLockHistory> Entries { get; init; } = [];
    public int Limit { get; init; }
    public string? ErrorMessage { get; init; }
}
