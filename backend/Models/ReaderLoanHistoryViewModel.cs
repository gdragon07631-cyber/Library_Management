using Project.Services;

namespace Project.Models;

public sealed record ReaderLateReturnEvaluation(
    int TotalLoanCount,
    int LateReturnCount,
    int OverdueOpenLoanCount,
    decimal LateReturnRate,
    bool IsFrequentLateReturner,
    string? WarningMessage);

public sealed class ReaderLoanHistoryViewModel
{
    public const int FrequentLateThresholdCount = 2;
    public const decimal FrequentLateThresholdRatePercent = 50m;

    public required ReaderAccount Reader { get; init; }
    public IReadOnlyList<BookLoan> Loans { get; init; } = [];
    public int OpenLoanCount { get; init; }
    public int TotalLoanCount { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public bool IsFiltered => StartDate.HasValue || EndDate.HasValue;
    public string? DateFilterError { get; init; }

    public int LateReturnCount { get; init; }
    public int OverdueOpenLoanCount { get; init; }
    public int AllTimeTotalLoanCount { get; init; }
    public int AllTimeLateReturnCount { get; init; }
    public int AllTimeOverdueOpenLoanCount { get; init; }
    public decimal LateReturnRate { get; init; }
    public bool IsFrequentLateReturner { get; init; }
    public string? FrequentLateWarningMessage { get; init; }

    public static bool IsLateReturn(BookLoan loan)
    {
        if (loan.CloseReason is BookLoanCloseReason.Lost or BookLoanCloseReason.Damaged)
            return false;
        var returnedDate = GetLocalReturnDate(loan);
        return (returnedDate.HasValue && returnedDate.Value > loan.DueDate) || loan.OverdueDaysAtReturn > 0;
    }

    public static DateOnly? GetLocalReturnDate(BookLoan loan)
    {
        if (loan.CloseReason is BookLoanCloseReason.Lost or BookLoanCloseReason.Damaged)
            return null;
        if (loan.ReturnedAtUtc.HasValue)
            return DateOnly.FromDateTime(HoldPickupExpirySchedule.GetVietnamLocalTime(loan.ReturnedAtUtc.Value));
        return loan.ReturnDate;
    }

    public static ReaderLateReturnEvaluation Evaluate(IReadOnlyCollection<BookLoan> allLoans, DateOnly today)
    {
        var total = allLoans.Count;
        var lateReturned = allLoans.Count(IsLateReturn);
        var overdueOpen = allLoans.Count(loan => !loan.IsReturned && loan.ReturnedAtUtc is null && loan.CloseReason is null && loan.DueDate < today);
        var totalViolations = lateReturned + overdueOpen;
        var rate = total > 0 ? Math.Round(totalViolations * 100m / total, 1) : 0m;

        var isFrequent = lateReturned >= FrequentLateThresholdCount
            || totalViolations >= FrequentLateThresholdCount
            || (lateReturned >= 1 && total >= 2 && rate >= FrequentLateThresholdRatePercent);

        string? warning = null;
        if (isFrequent)
        {
            warning = overdueOpen > 0
                ? $"Cảnh báo: Bạn đọc thường xuyên trả trễ ({lateReturned} lần đã trả trễ, {overdueOpen} phiếu đang quá hạn chưa trả trên tổng số {total} lượt mượn — tỷ lệ trễ {rate:0.#}%). Vui lòng cân nhắc trước khi quyết định cho mượn tiếp."
                : $"Cảnh báo: Bạn đọc thường xuyên trả trễ ({lateReturned}/{total} lượt mượn trả trễ hạn — tỷ lệ {rate:0.#}%). Vui lòng cân nhắc trước khi quyết định cho mượn tiếp.";
        }

        return new ReaderLateReturnEvaluation(total, lateReturned, overdueOpen, rate, isFrequent, warning);
    }
}

