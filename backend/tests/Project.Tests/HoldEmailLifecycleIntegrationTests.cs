using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class HoldEmailLifecycleIntegrationTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;
    private readonly MutableTimeProvider clock = new(UtcForVietnam(2026, 10, 18, 8, 30));
    private readonly FakeTransport transport = new();

    public HoldEmailLifecycleIntegrationTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task ReadyReminderAndExpiryEventsSendOnceWithTheirOwnContentAndHistory()
    {
        var (ready, readyRow) = await AddHoldAsync(BookHoldStatus.Available, UtcForVietnam(2026, 10, 20, 17, 0), "HoldReady");
        var (reminder, reminderRow) = await AddHoldAsync(BookHoldStatus.Available, UtcForVietnam(2026, 10, 18, 23, 59), "HoldPickupLastDay");
        var (expired, expiredRow) = await AddHoldAsync(BookHoldStatus.Cancelled, UtcForVietnam(2026, 10, 17, 17, 0), "HoldExpired");
        expired.CancellationReason = HoldPickupExpiryService.ExpiredReason;
        expired.CancelledAtUtc = clock.GetUtcNow().UtcDateTime;
        expiredRow.EventOccurredAtUtc = expired.CancelledAtUtc;
        expiredRow.PickupDeadlineUtcSnapshot = UtcForVietnam(2026, 10, 17, 17, 0);
        expiredRow.BookTitleSnapshot = expired.Book!.Title;
        await db.SaveChangesAsync();

        var processor = CreateProcessor();
        Assert.True(await processor.ProcessOneAsync());
        Assert.True(await processor.ProcessOneAsync());
        Assert.True(await processor.ProcessOneAsync());
        Assert.False(await processor.ProcessOneAsync());

        db.ChangeTracker.Clear();
        Assert.Equal(3, transport.Messages.Count);
        Assert.All(transport.Messages, message => Assert.EndsWith("@example.test", message.Recipient));
        var readyMessage = Assert.Single(transport.Messages, message => message.Subject == readyRow.Subject);
        Assert.Contains(ready.Book!.Title, readyMessage.Body);
        Assert.Contains("Test warehouse", readyMessage.Body);
        Assert.Contains("HoldReady-", readyMessage.Body);
        var reminderMessage = Assert.Single(transport.Messages, message => message.Subject == reminderRow.Subject);
        Assert.Contains("ng\u00E0y cu\u1ED1i c\u00F9ng", reminderMessage.Body);
        Assert.Contains("18/10/2026 23:59", reminderMessage.Body);
        var expiredMessage = Assert.Single(transport.Messages, message => message.Subject == expiredRow.Subject);
        Assert.Contains("Th\u1EDDi \u0111i\u1EC3m h\u1EE7y", expiredMessage.Body);
        Assert.Contains("Qu\u00E1 h\u1EA1n nh\u1EADn s\u00E1ch", WebUtility.HtmlDecode(expiredMessage.Body));
        Assert.Contains("18/10/2026 08:30", expiredMessage.Body);
        Assert.All(await db.EmailSendHistories.ToListAsync(), row =>
        {
            Assert.Equal("Sent", row.Status);
            Assert.Equal(1, row.Attempts);
            Assert.NotNull(row.CompletedAtUtc);
        });
        Assert.Equal(ready.Id, readyRow.HoldId);
        Assert.Equal(reminder.Id, reminderRow.HoldId);
        Assert.Equal(expired.Id, expiredRow.HoldId);
    }

    [Fact]
    public async Task HoldThatChangesStateBeforeProcessingIsSkippedWithoutSending()
    {
        var (hold, row) = await AddHoldAsync(BookHoldStatus.ConvertedToLoan,
            UtcForVietnam(2026, 10, 18, 23, 59), "HoldPickupLastDay");

        Assert.True(await CreateProcessor().ProcessOneAsync());

        db.ChangeTracker.Clear();
        var stored = await db.EmailSendHistories.SingleAsync(item => item.Id == row.Id);
        Assert.Equal("Skipped", stored.Status);
        Assert.Equal(1, stored.Attempts);
        Assert.Empty(transport.Messages);
        Assert.Equal(BookHoldStatus.ConvertedToLoan, (await db.BookHolds.SingleAsync(item => item.Id == hold.Id)).Status);
    }

    [Fact]
    public async Task FailedDeliveryCanRetryWithoutChangingHoldStateOrLosingAttemptHistory()
    {
        var (hold, row) = await AddHoldAsync(BookHoldStatus.Available,
            UtcForVietnam(2026, 10, 20, 17, 0), "HoldReady");
        transport.FailNext = true;
        var processor = CreateProcessor();

        Assert.True(await processor.ProcessOneAsync());
        db.ChangeTracker.Clear();
        var failedAttempt = await db.EmailSendHistories.SingleAsync(item => item.Id == row.Id);
        Assert.Equal("Retrying", failedAttempt.Status);
        Assert.Equal(1, failedAttempt.Attempts);
        Assert.Contains("temporary SMTP error", failedAttempt.ErrorMessage);
        Assert.Equal(BookHoldStatus.Available, (await db.BookHolds.SingleAsync(item => item.Id == hold.Id)).Status);

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True(await processor.ProcessOneAsync());
        db.ChangeTracker.Clear();
        var sent = await db.EmailSendHistories.SingleAsync(item => item.Id == row.Id);
        Assert.Equal("Sent", sent.Status);
        Assert.Equal(2, sent.Attempts);
        Assert.Single(transport.Messages);
        Assert.Equal(BookHoldStatus.Available, (await db.BookHolds.SingleAsync(item => item.Id == hold.Id)).Status);
    }

    [Theory]
    [InlineData("HoldPickupLastDay")]
    [InlineData("HoldExpired")]
    public async Task ReminderAndExpiryDeliveryFailuresCanRetryWithoutSendingRealEmail(string notificationType)
    {
        var status = notificationType == "HoldExpired" ? BookHoldStatus.Cancelled : BookHoldStatus.Available;
        var deadline = notificationType == "HoldExpired"
            ? UtcForVietnam(2026, 10, 17, 17, 0)
            : UtcForVietnam(2026, 10, 18, 23, 59);
        var (hold, row) = await AddHoldAsync(status, deadline, notificationType);
        transport.FailNext = true;
        var processor = CreateProcessor();

        Assert.True(await processor.ProcessOneAsync());
        db.ChangeTracker.Clear();
        var failedAttempt = await db.EmailSendHistories.SingleAsync(item => item.Id == row.Id);
        Assert.Equal("Retrying", failedAttempt.Status);
        Assert.Equal(1, failedAttempt.Attempts);
        Assert.Contains("temporary SMTP error", failedAttempt.ErrorMessage);
        Assert.Empty(transport.Messages);

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True(await processor.ProcessOneAsync());
        db.ChangeTracker.Clear();
        var sent = await db.EmailSendHistories.SingleAsync(item => item.Id == row.Id);
        Assert.Equal("Sent", sent.Status);
        Assert.Equal(2, sent.Attempts);
        Assert.Single(transport.Messages);
        Assert.Equal(status, (await db.BookHolds.SingleAsync(item => item.Id == hold.Id)).Status);
    }

    [Fact]
    public async Task ManualCancellationDoesNotSendOrMisreportOverdueCancellationEmail()
    {
        var (hold, row) = await AddHoldAsync(BookHoldStatus.Cancelled,
            UtcForVietnam(2026, 10, 17, 17, 0), "HoldExpired");
        hold.CancellationReason = "Cancelled by staff";
        hold.CancelledAtUtc = clock.GetUtcNow().UtcDateTime;
        row.EventOccurredAtUtc = hold.CancelledAtUtc;
        row.PickupDeadlineUtcSnapshot = UtcForVietnam(2026, 10, 17, 17, 0);
        row.BookTitleSnapshot = hold.Book!.Title;
        await db.SaveChangesAsync();

        Assert.True(await CreateProcessor().ProcessOneAsync());

        db.ChangeTracker.Clear();
        Assert.Equal("Skipped", (await db.EmailSendHistories.SingleAsync()).Status);
        Assert.Empty(transport.Messages);
    }

    [Fact]
    public async Task OverdueCancellationEmailLinksToPublicBookDetailsWhenBaseUrlIsConfigured()
    {
        var (hold, row) = await AddHoldAsync(BookHoldStatus.Cancelled,
            UtcForVietnam(2026, 10, 17, 17, 0), "HoldExpired");

        Assert.True(await CreateProcessor("https://library.example.test").ProcessOneAsync());

        var message = Assert.Single(transport.Messages);
        Assert.Equal(row.Recipient, message.Recipient);
        Assert.Contains($"https://library.example.test/Book/Details/{hold.BookId}", message.Body);
    }

    private HoldEmailOutboxProcessor CreateProcessor(string? publicBaseUrl = null) => new(db, transport,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PublicBaseUrl"] = publicBaseUrl
        }).Build(), clock,
        NullLogger<HoldEmailOutboxProcessor>.Instance);

    private async Task<(BookHold Hold, EmailSendHistory Row)> AddHoldAsync(string status, DateTime deadlineUtc,
        string notificationType)
    {
        var unique = Guid.NewGuid().ToString("N");
        var reader = new ReaderAccount
        {
            FullName = "Test Reader", DateOfBirth = new DateOnly(2000, 1, 1), Email = $"{unique}@example.test",
            PhoneNumber = "0900000000", StudentOrStaffCode = unique, PasswordHash = "test", Status = "Active"
        };
        var book = new Book { Title = $"{notificationType}-{unique}", Author = new Author { Name = $"Author-{unique}" } };
        var shelf = new Shelf
        {
            Warehouse = new Warehouse { Code = $"W{unique[..6]}", Name = "Test warehouse" },
            Code = unique, Name = $"{notificationType} shelf"
        };
        var copy = new BookCopy { Book = book, Shelf = shelf, CopyCode = $"C{unique[..8]}", Status = BookCopyStatus.OnHold };
        var hold = new BookHold
        {
            Book = book, ReaderAccount = reader, Status = status, BookCopy = status == BookHoldStatus.Cancelled ? null : copy,
            PickupDeadlineUtc = status == BookHoldStatus.Cancelled ? null : deadlineUtc, HeldAtUtc = clock.GetUtcNow().UtcDateTime.AddDays(-2)
        };
        if (status == BookHoldStatus.Cancelled)
        {
            hold.CancellationReason = notificationType == "HoldExpired"
                ? HoldPickupExpiryService.ExpiredReason
                : "Cancelled by staff";
            hold.CancelledAtUtc = clock.GetUtcNow().UtcDateTime;
        }
        db.BookHolds.Add(hold);
        await db.SaveChangesAsync();
        var row = new EmailSendHistory
        {
            DeduplicationKey = Guid.NewGuid().ToString("N"), ReaderAccountId = reader.Id, HoldId = hold.Id,
            Recipient = reader.Email, NotificationType = notificationType, Subject = $"Subject {notificationType}",
            StartedAtUtc = clock.GetUtcNow().UtcDateTime, Status = "Queued",
            EventOccurredAtUtc = notificationType == "HoldExpired" ? hold.CancelledAtUtc : null,
            PickupDeadlineUtcSnapshot = notificationType == "HoldExpired" ? deadlineUtc : null,
            BookTitleSnapshot = notificationType == "HoldExpired" ? book.Title : null
        };
        db.EmailSendHistories.Add(row);
        await db.SaveChangesAsync();
        return (hold, row);
    }

    private static DateTime UtcForVietnam(int year, int month, int day, int hour, int minute) =>
        HoldPickupExpirySchedule.ToUtc(new DateOnly(year, month, day), new TimeOnly(hour, minute));

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    private sealed class MutableTimeProvider(DateTime utcNow) : TimeProvider
    {
        private DateTime now = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        public override DateTimeOffset GetUtcNow() => new(now);
        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }

    private sealed class FakeTransport : IHoldNotificationEmailTransport
    {
        public List<(string Recipient, string Subject, string Body)> Messages { get; } = [];
        public bool FailNext { get; set; }

        public Task SendAsync(string recipient, string subject, string htmlBody, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("temporary SMTP error");
            }
            Messages.Add((recipient, subject, htmlBody));
            return Task.CompletedTask;
        }
    }
}
