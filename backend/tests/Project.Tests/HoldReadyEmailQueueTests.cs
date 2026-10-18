using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class HoldReadyEmailQueueTests
{
    [Fact]
    public void EventKey_IsStableForSameTransition_AndDifferentForLaterTransition()
    {
        var hold = new BookHold
        {
            Id = 42,
            ReaderAccountId = 7,
            ReaderAccount = new ReaderAccount { Email = "reader@example.test" }
        };
        var firstTransition = new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);

        var first = HoldReadyEmailQueue.Create(hold, firstTransition);
        var duplicate = HoldReadyEmailQueue.Create(hold, firstTransition);
        var later = HoldReadyEmailQueue.Create(hold, firstTransition.AddDays(1));

        Assert.Equal(first.DeduplicationKey, duplicate.DeduplicationKey);
        Assert.NotEqual(first.DeduplicationKey, later.DeduplicationKey);
        Assert.Equal(42, first.HoldId);
        Assert.Equal("HoldReady", first.NotificationType);
        Assert.Equal("Th\u00F4ng b\u00E1o s\u00E1ch \u0111\u00E3 s\u1EB5n s\u00E0ng nh\u1EADn", first.Subject);
        Assert.Equal("Queued", first.Status);
        Assert.Equal("reader@example.test", first.Recipient);
    }
}
