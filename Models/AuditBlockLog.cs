using System.ComponentModel.DataAnnotations;

namespace Project.Models;

/// <summary>
/// Bảng ghi nhận chi tiết khi hệ thống chặn yêu cầu lập phiếu mượn.
/// Lưu các thông tin cần thiết để tra cứu: bạn đọc, thời điểm, số sách đang mượn, hạn mức, lý do, thủ thư thực hiện, IP.
/// </summary>
public sealed class AuditBlockLog
{
    public long Id { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    // Thông tin bạn đọc
    public int ReaderAccountId { get; set; }

    [Required, MaxLength(64)]
    public string ReaderCode { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string ReaderName { get; set; } = string.Empty;

    // Trạng thái mượn
    public int BorrowedCount { get; set; }
    public int BorrowingLimit { get; set; }
    public int OverdueLoanCount { get; set; }
    public decimal OutstandingBalance { get; set; }

    // Thông tin thẻ
    [MaxLength(50)]
    public string? CardStatus { get; set; }
    public DateOnly? CardExpiresOn { get; set; }

    // Lý do chặn (có thể nhiều lý do, lưu gộp trong một chuỗi phân tách ';')
    [Required, MaxLength(1000)]
    public string Reasons { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? BlockingReasonCodes { get; set; }

    [MaxLength(40)]
    public string EventType { get; set; } = LoanBlockHistoryEventTypes.BlockedOther;

    // Thông tin thủ thư thực hiện thao tác (nếu xác định được)
    [MaxLength(256)]
    public string? Actor { get; set; }

    [MaxLength(30)]
    public string? ActorRole { get; set; }

    [MaxLength(45)]
    public string? IpAddress { get; set; }

    public Guid? OverrideRequestId { get; set; }

    [MaxLength(1000)]
    public string? OverrideReason { get; set; }

    [MaxLength(20)]
    public string Outcome { get; set; } = "Blocked";

    [MaxLength(500)]
    public string? FailureReason { get; set; }

    public long? BookLoanId { get; set; }
}
