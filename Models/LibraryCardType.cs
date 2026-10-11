using System.ComponentModel.DataAnnotations;

namespace Project.Models;

public sealed class LibraryCardType
{
    public const int DefaultMaxRenewals = 3;
    public const int DefaultMaxBorrowedBooks = 100;

    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public int MaxRenewals { get; set; } = DefaultMaxRenewals;

    public int MaxBorrowedBooks { get; set; } = DefaultMaxBorrowedBooks;

    public ICollection<LibraryCard> LibraryCards { get; set; } = [];
}
