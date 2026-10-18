using System.Globalization;

namespace Project.Models;

public sealed record FeeReceiptViewModel(long PaymentId, int ReaderId, DateTime PaidAtUtc,
    string ReaderName, string CollectedByName, decimal Amount, IReadOnlyList<FeeReceiptLineViewModel> Lines)
{
    public string Number => "BL-" + PaymentId.ToString("D8", CultureInfo.InvariantCulture);
    public DateTimeOffset PaidAtLocal => new DateTimeOffset(DateTime.SpecifyKind(PaidAtUtc, DateTimeKind.Utc)).ToOffset(TimeSpan.FromHours(7));
}

public sealed record FeeReceiptLineViewModel(int ReaderFeeId, string Description, decimal Amount);
public sealed record FeeReceiptResult(FeeReceiptViewModel? Receipt, bool PaymentExists, string? Error = null);
