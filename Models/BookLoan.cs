using System.ComponentModel.DataAnnotations;

namespace Project.Models;

public sealed class BookLoan
{
    public long Id { get; set; }
    public int BookId { get; set; }
    public Book? Book { get; set; }
    public long? BookCopyId { get; set; }
    public BookCopy? BookCopy { get; set; }
    public int ReaderAccountId { get; set; }
    public ReaderAccount? ReaderAccount { get; set; }
    public DateOnly LoanDate { get; set; }
    public DateOnly OriginalDueDate { get; set; }
    public DateOnly DueDate { get; set; }
    public int RenewalCount { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ReturnedAtUtc { get; set; }
}

public sealed class CreateBookLoanViewModel
{
    [Range(1, int.MaxValue, ErrorMessage = "Vui lòng chọn sách.")]
    public int BookId { get; set; }
    public long? BookCopyId { get; set; }
    [Range(1, int.MaxValue, ErrorMessage = "Vui lòng chọn bạn đọc.")]
    public int ReaderAccountId { get; set; }
    [Required(ErrorMessage = "Vui lòng chọn ngày mượn.")]
    public DateOnly? LoanDate { get; set; }
}

public sealed class LoanIndexViewModel
{
    public IReadOnlyList<BookLoan> Loans { get; set; } = [];
    public IReadOnlyList<Book> Books { get; set; } = [];
    public IReadOnlyList<BookCopy> AvailableCopies { get; set; } = [];
    public IReadOnlyList<ReaderAccount> Readers { get; set; } = [];
    public CreateBookLoanViewModel NewLoan { get; set; } = new();
}
