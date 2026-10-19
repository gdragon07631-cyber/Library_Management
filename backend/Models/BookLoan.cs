using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Project.Models;

public sealed class BookLoan
{
    public long Id { get; set; }
    public int BookId { get; set; }
    public Book? Book { get; set; }
    /// <summary>Specific physical copy issued for this loan, when created from a hold.</summary>
    public long? BookCopyId { get; set; }
    public BookCopy? BookCopy { get; set; }
    public long? SourceBookHoldId { get; set; }
    public BookHold? SourceBookHold { get; set; }
    public int? CreatedByAdminAccountId { get; set; }
    public AdminAccount? CreatedByAdminAccount { get; set; }
    public ICollection<LoanContactHistory> ContactHistories { get; set; } = [];
    public int ReaderAccountId { get; set; }
    public ReaderAccount? ReaderAccount { get; set; }
    public DateOnly LoanDate { get; set; }
    public DateOnly OriginalDueDate { get; set; }
    public DateOnly DueDate { get; set; }
    public int RenewalCount { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ReturnedAtUtc { get; set; }

    /// <summary>Lý do đóng phiếu mượn: Đã trả, Mất, hoặc Hỏng nặng (Lát 3).</summary>
    [MaxLength(50)]
    public string? CloseReason { get; set; }

    /// <summary>Khoản phí trễ hạn đã phát sinh tính đến ngày đóng phiếu mượn (Lát 3).</summary>
    public decimal OverdueFee { get; set; } = 0m;

    /// <summary>Calendar opening days charged at return.</summary>
    public int OverdueDaysAtReturn { get; set; }

    /// <summary>Daily rate captured at return so the finalized calculation is auditable.</summary>
    public decimal DailyOverdueFeeAtReturn { get; set; }

    public ReaderFee? OverdueReceivable { get; set; }

    /// <summary>Đơn giá phí trễ hạn mỗi ngày theo quy định thư viện (2.000 VNĐ / ngày).</summary>
    public const decimal DailyOverdueFeeRate = 2000m;

    [NotMapped]
    public DateOnly? ReturnDate { get; set; }

    [NotMapped]
    public DateOnly? ReturnedDate
    {
        get => ReturnDate;
        set => ReturnDate = value;
    }

    [NotMapped]
    public string? Status { get; set; }

    [NotMapped]
    public bool IsReturned
    {
        get => _isReturned
            || ReturnedAtUtc.HasValue
            || ReturnDate.HasValue
            || !string.IsNullOrEmpty(CloseReason)
            || string.Equals(Status, "Đã trả", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Status, "Da tra", StringComparison.OrdinalIgnoreCase);
        set => _isReturned = value;
    }
    private bool _isReturned;
}

public static class BookLoanCloseReason
{
    public const string Returned = "Đã trả";
    public const string Lost = "Mất";
    public const string Damaged = "Hỏng nặng";

    public static readonly IReadOnlyList<string> All = [Returned, Lost, Damaged];
}

public sealed class CreateBookLoanViewModel : IValidatableObject
{
    public int BookId { get; set; }
    public long? BookCopyId { get; set; }
    public List<int>? BookIds { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Vui lòng chọn bạn đọc.")]
    public int ReaderAccountId { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn ngày mượn.")]
    public DateOnly? LoanDate { get; set; }

    public string? BypassReason { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var hasSingle = BookId > 0;
        var hasMultiple = BookIds != null && BookIds.Count > 0 && BookIds.All(id => id > 0);
        if (!hasSingle && !hasMultiple)
        {
            yield return new ValidationResult("Vui lòng chọn sách.", [nameof(BookId)]);
        }
    }
}

public sealed class CreateBatchBookLoanViewModel
{
    public List<int> BookIds { get; set; } = [];

    [Range(1, int.MaxValue, ErrorMessage = "Vui lòng chọn bạn đọc.")]
    public int ReaderAccountId { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn ngày mượn.")]
    public DateOnly? LoanDate { get; set; }

    public string? BypassReason { get; set; }
}

public sealed class OverrideBookLoanViewModel : IValidatableObject
{
    public int BookId { get; set; }
    public List<int>? BookIds { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Vui lòng chọn bạn đọc.")]
    public int ReaderAccountId { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn ngày mượn.")]
    public DateOnly? LoanDate { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập lý do bỏ qua."), MaxLength(500)]
    public string BypassReason { get; set; } = string.Empty;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var hasSingle = BookId > 0;
        var hasMultiple = BookIds != null && BookIds.Count > 0 && BookIds.All(id => id > 0);
        if (!hasSingle && !hasMultiple)
        {
            yield return new ValidationResult("Vui lòng chọn sách.", [nameof(BookId)]);
        }

        if (string.IsNullOrWhiteSpace(BypassReason))
        {
            yield return new ValidationResult("Vui lòng nhập lý do bỏ qua.", [nameof(BypassReason)]);
        }
    }
}

public sealed class BlockedLoanLogEntry
{
    public long Id { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string Operator { get; set; } = string.Empty;
    public int ReaderAccountId { get; set; }
    public string ReaderName { get; set; } = string.Empty;
    public string ReaderEmail { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string? BypassReason { get; set; }
    public string Status { get; set; } = "Bị chặn";
    public bool IsOverridden => Status == "Đã bỏ qua";
    public string Target { get; set; } = string.Empty;
}

public sealed class LoanIndexViewModel
{
    public IReadOnlyList<BookLoan> Loans { get; set; } = [];
    public IReadOnlyList<Book> Books { get; set; } = [];
    public IReadOnlyList<BookCopy> AvailableCopies { get; set; } = [];
    public IReadOnlyList<ReaderAccount> Readers { get; set; } = [];
    public IReadOnlyDictionary<int, int> ReaderLoanCounts { get; set; } = new Dictionary<int, int>();
    public IReadOnlySet<int> ReaderHasOverdue { get; set; } = new HashSet<int>();
    public IReadOnlyDictionary<int, int> ReaderLateReturnCounts { get; set; } = new Dictionary<int, int>();
    public IReadOnlyDictionary<int, decimal> ReaderLateReturnRates { get; set; } = new Dictionary<int, decimal>();
    public IReadOnlySet<int> ReaderFrequentLateIds { get; set; } = new HashSet<int>();
    public IReadOnlyList<BlockedLoanLogEntry> BlockedLoanLogs { get; set; } = [];
    public CreateBookLoanViewModel NewLoan { get; set; } = new();
    public bool CanOverride { get; set; }
}

/// <summary>Thông tin liên hệ cần thiết để thủ thư nhắc các phiếu mượn quá hạn.</summary>
public sealed record OverdueLoanItem(
    long LoanId,
    int DaysOverdue,
    string ReaderName,
    string PhoneNumber,
    string? CardCode,
    string BookTitle,
    DateOnly LoanDate,
    DateOnly DueDate,
    string? CopyBarcode,
    LoanContactHistoryItem? LatestContact);

public sealed record LoanContactHistoryItem(
    long Id,
    long LoanId,
    DateTime CreatedAtUtc,
    string Note,
    int? ContactedByAdminAccountId,
    string ContactedBy);

public sealed class CreateLoanContactHistoryViewModel
{
    public long LoanId { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập nội dung ghi chú liên hệ."), MaxLength(1000)]
    public string Note { get; set; } = string.Empty;
}

public sealed class LoanContactHistoryPageViewModel
{
    public long LoanId { get; init; }
    public IReadOnlyList<LoanContactHistoryItem> Items { get; init; } = [];
}

/// <summary>Các khoảng ngày trễ được hỗ trợ trên danh sách nhắc hạn.</summary>
public enum OverdueLoanRange
{
    All,
    OneToSevenDays,
    MoreThanSevenDays,
    MoreThanThirtyDays
}

public static class OverdueLoanRanges
{
    public const string All = "all";
    public const string OneToSevenDays = "1-7";
    public const string MoreThanSevenDays = "over-7";
    public const string MoreThanThirtyDays = "over-30";

    public static OverdueLoanRange Parse(string? value) => value?.Trim() switch
    {
        OneToSevenDays => OverdueLoanRange.OneToSevenDays,
        MoreThanSevenDays => OverdueLoanRange.MoreThanSevenDays,
        MoreThanThirtyDays => OverdueLoanRange.MoreThanThirtyDays,
        _ => OverdueLoanRange.All
    };

    public static string ToQueryValue(OverdueLoanRange range) => range switch
    {
        OverdueLoanRange.OneToSevenDays => OneToSevenDays,
        OverdueLoanRange.MoreThanSevenDays => MoreThanSevenDays,
        OverdueLoanRange.MoreThanThirtyDays => MoreThanThirtyDays,
        _ => All
    };
}

public sealed class OverdueLoanViewModel
{
    public IReadOnlyList<OverdueLoanItem> Items { get; init; } = [];
    public OverdueLoanRange Range { get; init; } = OverdueLoanRange.All;
}
