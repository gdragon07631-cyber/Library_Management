using System.ComponentModel.DataAnnotations;

namespace Project.Models;

public static class IncidentAuditOperations
{
    public const string RecordIncident = "Ghi nhận sự cố";
    public const string CloseLoan = "Đóng phiếu mượn";
    public const string CalculateCompensation = "Tính bồi thường";
    public const string AdjustCompensation = "Điều chỉnh bồi thường";

    public static readonly IReadOnlyList<string> All =
    [
        RecordIncident,
        CloseLoan,
        CalculateCompensation,
        AdjustCompensation
    ];

    public static string BadgeClass(string operation) => operation switch
    {
        RecordIncident => "bg-danger-subtle text-danger-emphasis",
        CloseLoan => "bg-secondary-subtle text-secondary-emphasis",
        CalculateCompensation => "bg-info-subtle text-info-emphasis",
        AdjustCompensation => "bg-warning-subtle text-warning-emphasis",
        _ => "bg-primary-subtle text-primary-emphasis"
    };
}

/// <summary>
/// Nhật ký nghiệp vụ không thể chỉnh sửa hoặc xóa cho quy trình xử lý sự cố sách (Lát 4).
/// Lưu vết đầy đủ: Ghi nhận sự cố, Đóng phiếu mượn, Tính bồi thường, Điều chỉnh bồi thường.
/// </summary>
public sealed class BookIncidentAuditLog
{
    public long Id { get; set; }

    public long BookLoanId { get; set; }
    public BookLoan? BookLoan { get; set; }

    public long? BookIncidentId { get; set; }
    public BookIncident? BookIncident { get; set; }

    public long? BookCopyId { get; set; }
    public BookCopy? BookCopy { get; set; }

    [Required, MaxLength(100)]
    public string OperationType { get; set; } = string.Empty;

    [Required, MaxLength(256)]
    public string Actor { get; set; } = string.Empty;

    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;

    // Chi tiết sự cố (Mất hoặc Hỏng nặng)
    [MaxLength(50)]
    public string? IncidentType { get; set; }

    [MaxLength(1000)]
    public string? Description { get; set; }

    [MaxLength(500)]
    public string? PhotoPath { get; set; }

    [MaxLength(50)]
    public string? CopyStatusResult { get; set; }

    // Chi tiết đóng phiếu mượn & phí trễ hạn
    [MaxLength(50)]
    public string? CloseReason { get; set; }

    public DateOnly? IncidentDate { get; set; }

    public decimal? OverdueFee { get; set; }

    // Chi tiết tính và điều chỉnh khoản bồi thường
    public decimal? RecordedCoverPrice { get; set; }
    public decimal? AppliedRate { get; set; }
    public decimal? InitialCompensationAmount { get; set; }
    public decimal? FromAmount { get; set; }
    public decimal? ToAmount { get; set; }

    [MaxLength(1000)]
    public string? AdjustmentReason { get; set; }

    [Required, MaxLength(1000)]
    public string Summary { get; set; } = string.Empty;
}

public sealed class LoanIncidentHistoryViewModel
{
    public BookLoan Loan { get; set; } = null!;
    public BookIncident? Incident { get; set; }
    public BookIncidentCompensation? Compensation { get; set; }
    public IReadOnlyList<BookIncidentAuditLog> Logs { get; set; } = [];
    public IReadOnlyList<IncidentAdjustmentVoucher> Vouchers { get; set; } = [];
}

