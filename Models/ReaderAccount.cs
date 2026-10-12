using System.ComponentModel.DataAnnotations;

namespace Project.Models;

public sealed class ReaderAccount
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string FullName { get; set; } = string.Empty;

    [Required]
    public DateOnly DateOfBirth { get; set; }

    [Required, EmailAddress, MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required, MaxLength(20)]
    public string PhoneNumber { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Address { get; set; }

    [Required, MaxLength(50)]
    public string StudentOrStaffCode { get; set; } = string.Empty;

    [Required, MaxLength(512)]
    public string PasswordHash { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string Status { get; set; } = "Chờ duyệt";

    [MaxLength(1000)]
    public string? RejectionReason { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public int SessionVersion { get; set; }

    public decimal OutstandingBalance { get; set; }

    public LibraryCard? LibraryCard { get; set; }
    public ICollection<BookHold> BookHolds { get; set; } = [];
    public ICollection<ReaderPasswordHistory> PasswordHistories { get; set; } = [];
}
