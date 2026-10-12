using System.ComponentModel.DataAnnotations;

namespace Project.Models;

public static class BookCopyStatus
{
    public const string Available = "Sẵn sàng";
    public const string OnLoan = "Đang mượn";
    public const string Borrowed = OnLoan;
    /// <summary>Bản sao đang được giữ cho một đơn đặt giữ.</summary>
    public const string OnHold = "Đang giữ";
    public const string Held = OnHold;
    /// <summary>Bản sao đang sửa chữa: không cho mượn và không tính vào số bản rảnh.</summary>
    public const string UnderRepair = "Đang sửa chữa";
    public const string Removed = "Đã loại khỏi kho";

    /// <summary>Trạng thái thủ thư được chọn trực tiếp; "Đang mượn"/"Đang giữ" do nghiệp vụ mượn và đặt giữ quản lý.</summary>
    public static readonly IReadOnlyList<string> Editable = [Available, UnderRepair];
}

public static class BookCopyCondition
{
    public const string Good = "Tốt";
    public const string Worn = "Cũ, còn dùng tốt";
    public const string MinorDamage = "Hư hỏng nhẹ";
    public const string MajorDamage = "Hư hỏng nặng";

    public static readonly IReadOnlyList<string> All = [Good, Worn, MinorDamage, MajorDamage];
}

/// <summary>Lịch sử mỗi lần đổi trạng thái bản sao: ai, lúc nào, từ đâu sang đâu và vì sao.</summary>
public sealed class BookCopyStatusHistory
{
    public long Id { get; set; }
    public long BookCopyId { get; set; }
    public BookCopy? BookCopy { get; set; }

    [MaxLength(50)]
    public string FromStatus { get; set; } = string.Empty;

    [MaxLength(50)]
    public string ToStatus { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    [MaxLength(256)]
    public string ChangedBy { get; set; } = string.Empty;

    public DateTime ChangedAtUtc { get; set; }
}

public sealed class BookCopy
{
    public long Id { get; set; }
    public int BookId { get; set; }
    public Book? Book { get; set; }
    public int ShelfId { get; set; }
    public Shelf? Shelf { get; set; }

    [Required, MaxLength(50)]
    public string CopyCode { get; set; } = string.Empty;

    public DateOnly? ReceivedDate { get; set; }

    public decimal? CoverPrice { get; set; }

    [MaxLength(50)]
    public string Status { get; set; } = "Sẵn sàng";

    [MaxLength(50)]
    public string PhysicalCondition { get; set; } = BookCopyCondition.Good;

    [MaxLength(500)]
    public string? Note { get; set; }

    [MaxLength(500)]
    public string? StatusReason { get; set; }
}
