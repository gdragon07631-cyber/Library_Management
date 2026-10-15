using System.ComponentModel.DataAnnotations;

namespace Project.Models;

public static class IncidentAdjustmentTypes
{
    public const string FullReversal = "Hủy sự cố do ghi nhận nhầm";
    public const string CompensationAdjustment = "Điều chỉnh bồi thường và căn cứ";
}

public static class CopyReversalActions
{
    public const string RestoreToLoan = "Khôi phục đang mượn";
    public const string ReturnToInventory = "Nhập lại kho sẵn sàng";
}

/// <summary>
/// Bút toán điều chỉnh nghiệp vụ sự cố sách (Lát 5).
/// Lưu vết đầy đủ lý do, căn cứ, người lập, người xác nhận và trạng thái trước/sau điều chỉnh.
/// Dữ liệu là bất biến (không thể sửa hoặc xóa sau khi xác nhận).
/// </summary>
public sealed class IncidentAdjustmentVoucher
{
    public long Id { get; set; }

    [Required, MaxLength(50)]
    public string VoucherCode { get; set; } = string.Empty;

    public long BookIncidentId { get; set; }
    public BookIncident? BookIncident { get; set; }

    public long BookLoanId { get; set; }
    public BookLoan? BookLoan { get; set; }

    public long BookCopyId { get; set; }
    public BookCopy? BookCopy { get; set; }

    [Required, MaxLength(100)]
    public string AdjustmentType { get; set; } = IncidentAdjustmentTypes.FullReversal;

    [Required(ErrorMessage = "Vui lòng nhập lý do điều chỉnh."), MaxLength(1000)]
    public string Reason { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập tài liệu/biên bản căn cứ."), MaxLength(500)]
    public string ReferenceDocument { get; set; } = string.Empty;

    [Required, MaxLength(256)]
    public string CreatedBy { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? ApprovedBy { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    // Snapshot trạng thái trước điều chỉnh
    [MaxLength(50)]
    public string PreviousCopyStatus { get; set; } = BookCopyStatus.Removed;

    [MaxLength(50)]
    public string? PreviousLoanCloseReason { get; set; }

    public decimal PreviousCompensationAmount { get; set; }

    public decimal PreviousOverdueFee { get; set; }

    // Snapshot trạng thái sau điều chỉnh
    [MaxLength(50)]
    public string NewCopyStatus { get; set; } = BookCopyStatus.OnLoan;

    [MaxLength(50)]
    public string? NewLoanCloseReason { get; set; }

    public decimal NewCompensationAmount { get; set; }

    public decimal NewOverdueFee { get; set; }

    [MaxLength(50)]
    public string Status { get; set; } = "Đã xác nhận";

    [MaxLength(1000)]
    public string Notes { get; set; } = string.Empty;
}

public sealed class CreateAdjustmentVoucherRequest
{
    public long IncidentId { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập lý do điều chỉnh."), MaxLength(1000)]
    public string Reason { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập tài liệu/biên bản căn cứ."), MaxLength(500)]
    public string ReferenceDocument { get; set; } = string.Empty;

    public string CopyAction { get; set; } = CopyReversalActions.RestoreToLoan;

    public string StaffRole { get; set; } = string.Empty;
    public string StaffEmail { get; set; } = string.Empty;

    public string? Notes { get; set; }
}

public sealed class CreateAdjustmentVoucherOutcome
{
    public bool IsSuccess { get; private init; }
    public string? ErrorMessage { get; private init; }
    public IncidentAdjustmentVoucher? Voucher { get; private init; }

    public static CreateAdjustmentVoucherOutcome Success(IncidentAdjustmentVoucher voucher) =>
        new() { IsSuccess = true, Voucher = voucher };

    public static CreateAdjustmentVoucherOutcome Failed(string error) =>
        new() { IsSuccess = false, ErrorMessage = error };
}

public sealed class EligibleIncidentItemViewModel
{
    public long IncidentId { get; set; }
    public long LoanId { get; set; }
    public string BookTitle { get; set; } = string.Empty;
    public string CopyCode { get; set; } = string.Empty;
    public string ReaderName { get; set; } = string.Empty;
    public string IncidentType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal OverdueFee { get; set; }
    public decimal? CompensationAmount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string ReportedBy { get; set; } = string.Empty;
    public bool IsReversed { get; set; }
}

