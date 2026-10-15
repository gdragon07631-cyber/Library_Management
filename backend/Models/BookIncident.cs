using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace Project.Models;

public static class BookIncidentType
{
    public const string Lost = "Mất";
    public const string Damaged = "Hỏng nặng";

    public static readonly IReadOnlyList<string> All = [Lost, Damaged];

    public static bool IsValid(string? type) =>
        string.Equals(type, Lost, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, Damaged, StringComparison.OrdinalIgnoreCase);
}

public sealed class BookIncident
{
    public long Id { get; set; }

    public long BookLoanId { get; set; }
    public BookLoan? BookLoan { get; set; }

    public long BookCopyId { get; set; }
    public BookCopy? BookCopy { get; set; }

    [Required, MaxLength(50)]
    public string IncidentType { get; set; } = string.Empty;

    [Required, MaxLength(1000)]
    public string Description { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? PhotoPath { get; set; }

    [Required, MaxLength(256)]
    public string ReportedBy { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public BookIncidentCompensation? Compensation { get; set; }
}

public sealed class RecordIncidentRequest
{
    public long LoanId { get; set; }
    public long BookCopyId { get; set; }
    public string IncidentType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public IFormFile? Photo { get; set; }
    public string? StaffEmail { get; set; }
    public string? IpAddress { get; set; }
    public DateTime? IncidentDateUtc { get; set; }
}

public sealed class RecordIncidentOutcome
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
    public BookIncident? Incident { get; init; }
    public BookCopy? UpdatedCopy { get; init; }
    public BookLoan? ClosedLoan { get; init; }
    public BookIncidentCompensation? Compensation { get; init; }
    public string? CompensationMessage { get; init; }
    public decimal OverdueFee { get; init; }
    public string? CloseReason { get; init; }

    public static RecordIncidentOutcome Success(
        BookIncident incident,
        BookCopy copy,
        BookIncidentCompensation? compensation = null,
        string? compensationMessage = null,
        BookLoan? closedLoan = null,
        decimal overdueFee = 0m,
        string? closeReason = null) =>
        new()
        {
            IsSuccess = true,
            Incident = incident,
            UpdatedCopy = copy,
            ClosedLoan = closedLoan,
            Compensation = compensation,
            CompensationMessage = compensationMessage,
            OverdueFee = overdueFee,
            CloseReason = closeReason
        };

    public static RecordIncidentOutcome Failed(string message) =>
        new() { IsSuccess = false, ErrorMessage = message };
}

public sealed class RecordBookIncidentViewModel
{
    [Range(1, long.MaxValue, ErrorMessage = "Vui lòng chọn phiếu mượn đang mở.")]
    public long LoanId { get; set; }

    [Range(1, long.MaxValue, ErrorMessage = "Vui lòng chọn bản sao sách.")]
    public long BookCopyId { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn tình trạng (Mất hoặc Hỏng nặng).")]
    public string IncidentType { get; set; } = BookIncidentType.Lost;

    [Required(ErrorMessage = "Vui lòng nhập mô tả tình trạng."), MaxLength(1000, ErrorMessage = "Mô tả không được vượt quá 1000 ký tự.")]
    public string Description { get; set; } = string.Empty;

    public IFormFile? Photo { get; set; }
}

public sealed record OpenLoanItemViewModel(
    long LoanId,
    int BookId,
    string BookTitle,
    int ReaderAccountId,
    string ReaderName,
    string? CardCode,
    DateOnly LoanDate,
    DateOnly DueDate,
    long? BookCopyId,
    string? CopyBarcode,
    string? CopyStatus,
    string? PhysicalCondition);

public sealed record BookCopyIncidentItemViewModel(
    long CopyId,
    string CopyBarcode,
    string ShelfName,
    string PhysicalCondition,
    string Status,
    string BookTitle,
    decimal? CoverPrice = null);

public sealed class BookIncidentIndexViewModel
{
    public IReadOnlyList<OpenLoanItemViewModel> OpenLoans { get; set; } = [];
    public long? SelectedLoanId { get; set; }
    public IReadOnlyList<BookCopyIncidentItemViewModel> SelectedLoanCopies { get; set; } = [];
    public IReadOnlyList<BookIncident> RecentIncidents { get; set; } = [];
    public RecordBookIncidentViewModel FormModel { get; set; } = new();
    public CompensationPolicy? Policy { get; set; }
    public AdjustCompensationViewModel AdjustModel { get; set; } = new();
}

