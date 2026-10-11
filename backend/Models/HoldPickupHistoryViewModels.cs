namespace Project.Models;

public sealed class AutoCancelledHoldHistoryItem
{
    public long HoldId { get; set; }
    public string BookTitle { get; set; } = string.Empty;
    public string? ExpiredBookCopyCode { get; set; }
    public string ReaderName { get; set; } = string.Empty;
    public DateTime CancelledAtUtc { get; set; }
    public string CancellationReason { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? ExpiryCopyOutcome { get; set; }
    public long? ExpiryNextHoldId { get; set; }
}

public sealed class AutoCancelledHoldHistoryPageViewModel
{
    public IReadOnlyList<AutoCancelledHoldHistoryItem> Items { get; set; } = [];
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public int TotalCount { get; set; }
    public string? ErrorMessage { get; set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public bool HasPreviousPage => Page > 1;
    public bool HasNextPage => Page < TotalPages;
}
