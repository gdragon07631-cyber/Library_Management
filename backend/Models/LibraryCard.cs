using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Project.Models;

public sealed class LibraryCard
{
    public int Id { get; set; }

    [Required, MaxLength(32)]
    public string CardCode { get; set; } = string.Empty;

    public int ReaderAccountId { get; set; }
    public ReaderAccount? ReaderAccount { get; set; }

    public int LibraryCardTypeId { get; set; }
    public LibraryCardType? LibraryCardType { get; set; }

    public DateOnly IssuedOn { get; set; }
    public DateOnly ExpiresOn { get; set; }

    [Required, MaxLength(50)]
    public string Status { get; set; } = "Đang hoạt động";

    [MaxLength(1000)]
    public string? LockReason { get; set; }

    public DateTime? LockedAtUtc { get; set; }

    public DateTime? LockExpiresAtUtc { get; set; }

    public bool IsLockUntilManualUnlock { get; set; }

    public int? LockedByAdminAccountId { get; set; }
    public AdminAccount? LockedByAdminAccount { get; set; }

    [NotMapped]
    public bool IsLocked
    {
        get => _isLocked || LibraryCardStatusHelper.IsCardLocked(this, DateTime.UtcNow);
        set => _isLocked = value;
    }

    private bool _isLocked;
}

public static class LibraryCardStatusHelper
{
    public static bool IsCardLocked(LibraryCard? card, DateTime? referenceUtc = null)
    {
        if (card is null) return false;

        var reference = referenceUtc ?? DateTime.UtcNow;
        if (card.LockedAtUtc is not null)
        {
            if (card.IsLockUntilManualUnlock || !card.LockExpiresAtUtc.HasValue)
                return true;
            return card.LockExpiresAtUtc.Value > reference;
        }

        return card.Status is not null && (card.Status.Contains("khóa", StringComparison.OrdinalIgnoreCase)
            || card.Status.Contains("khoá", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsCardAvailable(LibraryCard? card, DateTime? referenceUtc = null)
    {
        if (card is null || IsCardLocked(card, referenceUtc)) return false;
        if (string.Equals(card.Status, "Đang hoạt động", StringComparison.OrdinalIgnoreCase)) return true;
        return IsTemporaryLockExpired(card, referenceUtc ?? DateTime.UtcNow);
    }

    public static string GetEffectiveStatus(LibraryCard card, DateTime? referenceUtc = null) =>
        IsTemporaryLockExpired(card, referenceUtc ?? DateTime.UtcNow) ? "Đang hoạt động" : card.Status;

    private static bool IsTemporaryLockExpired(LibraryCard card, DateTime referenceUtc) =>
        card.LockedAtUtc.HasValue &&
        !card.IsLockUntilManualUnlock &&
        card.LockExpiresAtUtc.HasValue &&
        card.LockExpiresAtUtc.Value <= referenceUtc;

    public static string GetLockMessage(LibraryCard? card, DateTime? referenceUtc = null)
    {
        if (!IsCardLocked(card, referenceUtc)) return string.Empty;
        if (card is null) return "Thẻ bạn đọc đang bị khoá.";

        var reason = string.IsNullOrWhiteSpace(card.LockReason)
            ? "Không có lý do cụ thể."
            : card.LockReason.Trim();

        if (card.IsLockUntilManualUnlock || !card.LockExpiresAtUtc.HasValue)
            return $"Thẻ bạn đọc đang bị khoá. Lý do: {reason}. Tới khi được mở khoá thủ công.";

        // Giá trị lưu theo UTC; hiển thị theo giờ thư viện để khớp với ngày giờ Quản lý đã chọn khi khoá.
        var expiresLocal = DateTime.SpecifyKind(card.LockExpiresAtUtc.Value, DateTimeKind.Utc).ToLocalTime();
        return $"Thẻ bạn đọc đang bị khoá. Lý do: {reason}. Hạn khoá: {expiresLocal:dd/MM/yyyy HH:mm}.";
    }
}
