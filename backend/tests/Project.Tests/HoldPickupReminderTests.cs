using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class HoldPickupReminderTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;

    public HoldPickupReminderTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public void ScheduleUsesVietnamTimeAndHasConfigurableMorningRun()
    {
        var before = UtcForVietnam(2026, 10, 18, 7, 59);
        var due = UtcForVietnam(2026, 10, 18, 8, 0);

        Assert.False(HoldPickupReminderSchedule.IsDue(before, new TimeOnly(8, 0)));
        Assert.True(HoldPickupReminderSchedule.IsDue(due, new TimeOnly(8, 0)));
        Assert.Equal(UtcForVietnam(2026, 10, 19, 8, 0),
            HoldPickupReminderSchedule.GetNextRunUtc(due, new TimeOnly(8, 0)));
        Assert.True(HoldPickupReminderSchedule.TryParseRunTime("07:45", out var parsed));
        Assert.Equal(new TimeOnly(7, 45), parsed);
        Assert.False(HoldPickupReminderSchedule.TryParseRunTime("25:00", out _));
    }

    [Fact]
    public async Task EnqueuesOnlyCurrentVietnamDeadlineDateOncePerHold()
    {
        var businessDate = new DateOnly(2026, 10, 18);
        var (book, copy) = await AddBookWithCopyAsync();
        var due = await AddReadyHoldAsync(book, copy,
            HoldPickupExpirySchedule.ToUtc(businessDate, new TimeOnly(23, 59)));
        await AddReadyHoldAsync(book, copy,
            HoldPickupExpirySchedule.ToUtc(businessDate.AddDays(1), new TimeOnly(12, 0)));
        await AddReadyHoldAsync(book, copy,
            HoldPickupExpirySchedule.ToUtc(businessDate, new TimeOnly(12, 0)), BookHoldStatus.Cancelled);

        var clock = new MutableTimeProvider(UtcForVietnam(2026, 10, 18, 8, 0));
        var enqueuer = new HoldPickupReminderEnqueuer(db, clock, NullLogger<HoldPickupReminderEnqueuer>.Instance);

        Assert.Equal(1, await enqueuer.EnqueueForDateAsync(businessDate));
        Assert.Equal(0, await enqueuer.EnqueueForDateAsync(businessDate));
        var notification = await db.EmailSendHistories.SingleAsync();
        Assert.Equal(due.Id, notification.HoldId);
        Assert.Equal("HoldPickupLastDay", notification.NotificationType);
        Assert.Equal("Nh\u1EAFc nh\u1EDF: H\u00F4m nay l\u00E0 h\u1EA1n cu\u1ED1i nh\u1EADn s\u00E1ch \u0111\u00E3 \u0111\u1EB7t gi\u1EEF", notification.Subject);
        Assert.Equal("Queued", notification.Status);
        Assert.Equal(due.ReaderAccount!.Email, notification.Recipient);
    }

    private async Task<(Book Book, BookCopy Copy)> AddBookWithCopyAsync()
    {
        var unique = Guid.NewGuid().ToString("N");
        var book = new Book { Title = "Book " + unique, Author = new Author { Name = "Author " + unique } };
        var shelf = new Shelf
        {
            Warehouse = new Warehouse { Code = "WH-" + unique[..6], Name = "Warehouse" },
            Code = unique, Name = "Pickup shelf"
        };
        var copy = new BookCopy { Book = book, Shelf = shelf, CopyCode = "COPY-" + unique[..6], Status = BookCopyStatus.OnHold };
        db.BookCopies.Add(copy);
        await db.SaveChangesAsync();
        return (book, copy);
    }

    private async Task<BookHold> AddReadyHoldAsync(Book book, BookCopy copy, DateTime deadlineUtc,
        string status = BookHoldStatus.Available)
    {
        var reader = new ReaderAccount
        {
            FullName = "Reader", DateOfBirth = new DateOnly(2000, 1, 1), Email = Guid.NewGuid() + "@example.com",
            PhoneNumber = "0900000000", StudentOrStaffCode = Guid.NewGuid().ToString(), PasswordHash = "hash",
            Status = "Đang hoạt động"
        };
        var hold = new BookHold
        {
            BookId = book.Id, ReaderAccount = reader, Status = status, BookCopyId = copy.Id,
            PickupDeadlineUtc = deadlineUtc, HeldAtUtc = DateTime.UtcNow.AddDays(-1)
        };
        db.BookHolds.Add(hold);
        await db.SaveChangesAsync();
        return hold;
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
        private readonly DateTime now = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
