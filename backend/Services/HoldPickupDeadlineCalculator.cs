using Project.Models;

namespace Project.Services;

public static class HoldPickupDeadlineCalculator
{
    public static async Task<DateTime> ThreeOpenDaysAfterAsync(
        DateTime startedAtUtc, IBookLoanService loanService, CancellationToken cancellationToken = default)
    {
        var deadlineDate = HoldPickupExpirySchedule.GetBusinessDate(startedAtUtc);
        for (var openDay = 0; openDay < 3; openDay++)
        {
            deadlineDate = await loanService.AdjustDueDateAsync(deadlineDate, cancellationToken);
            if (openDay < 2) deadlineDate = deadlineDate.AddDays(1);
        }

        return HoldPickupExpirySchedule.ToUtc(deadlineDate, ReaderRegistrationService.PickupDeadlineTime);
    }
}
