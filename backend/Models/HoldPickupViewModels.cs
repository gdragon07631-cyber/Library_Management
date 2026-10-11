namespace Project.Models;

public sealed class HoldPickupItemViewModel
{
    public long HoldId { get; set; }
    public int BookId { get; set; }
    public string BookTitle { get; set; } = string.Empty;
    public string? Isbn { get; set; }
    public long? BookCopyId { get; set; }
    public string CopyBarcode { get; set; } = string.Empty;
    public int ReaderAccountId { get; set; }
    public string ReaderName { get; set; } = string.Empty;
    public string LibraryCardCode { get; set; } = string.Empty;
    public string ReaderEmail { get; set; } = string.Empty;
    public string ReaderPhone { get; set; } = string.Empty;
    public DateTime? PickupDeadlineUtc { get; set; }
    public DateTime HeldAtUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? CancellationReason { get; set; }
    public string? CurrentShelf { get; set; }
    public string? CurrentWarehouse { get; set; }
    public bool IsPickupExpired =>
        (PickupDeadlineUtc is { } deadline && deadline < DateTime.UtcNow) ||
        (Status == BookHoldStatus.Cancelled && CancellationReason == BookHoldStatus.ExpiredCancellationReason);
    public string DisplayStatus => IsPickupExpired ? "Quá hạn nhận" : Status;
    public bool CanConfirm => BookHoldStatus.WaitingPickupStatuses.Contains(Status) && !IsPickupExpired;

    public string PickupDeadlineText => PickupDeadlineUtc is { } deadline
        ? deadline.ToString("dd/MM/yyyy HH:mm")
        : "—";
}

public sealed class ConfirmHoldPickupViewModel
{
    public long HoldId { get; set; }

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Vui lòng nhập mã thẻ bạn đọc.")]
    [System.ComponentModel.DataAnnotations.StringLength(32)]
    public string LibraryCardCode { get; set; } = string.Empty;
}

public sealed class HoldPickupListViewModel
{
    public IReadOnlyList<HoldPickupItemViewModel> Items { get; set; } = [];
    public bool CanProcessOverdue { get; set; }
    public int TotalCount => Items.Count;
}

