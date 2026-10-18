using Project.Services;

namespace Project.Tests;

public sealed class HoldExpiredEmailTemplateTests
{
    [Fact]
    public void BuildIncludesCancellationDetailsInVietnameseAndEscapesUserContent()
    {
        var body = HoldExpiredEmailTemplate.Build("Reader <One>", 42, "Book & Title",
            new DateTime(2026, 10, 18, 1, 30, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 18, 2, 0, 0, DateTimeKind.Utc),
            HoldPickupExpiryService.ExpiredReason);

        Assert.Contains("Reader &lt;One&gt;", body);
        Assert.Contains("Book &amp; Title", body);
        Assert.Contains("42", body);
        Assert.Contains("18/10/2026 08:30", body);
        Assert.Contains("18/10/2026 09:00", body);
        Assert.Contains("L\u00FD do h\u1EE7y:", body);
        Assert.Contains("gi\u1EDD Vi\u1EC7t Nam", body);
    }
}
