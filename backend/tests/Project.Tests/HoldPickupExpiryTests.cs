using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class HoldPickupExpiryTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;
    private readonly HoldPickupExpiryService service;

    public HoldPickupExpiryTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        var fulfillment = new BookHoldFulfillmentService(db, new BookLoanService(db, new WorkingScheduleService(db)));
        service = new HoldPickupExpiryService(db, fulfillment, NullLogger<HoldPickupExpiryService>.Instance);
    }

    [Fact]
    public async Task OverdueHoldIsCancelledAndCopyReturnsToShelf()
    {
        var (book, copy) = await AddBookWithCopyAsync();
        var hold = await AddReadyHoldAsync(book, copy, DateTime.UtcNow.AddMinutes(-1));

        var expired = await service.ExpireOverdueAsync(DateTime.UtcNow);

        Assert.Equal(1, expired);
        var stored = await db.BookHolds.AsNoTracking().SingleAsync(item => item.Id == hold.Id);
        Assert.Equal(BookHoldStatus.Cancelled, stored.Status);
        Assert.Equal(HoldPickupExpiryService.ExpiredReason, stored.CancellationReason);
        Assert.Null(stored.BookCopyId);
        Assert.Null(stored.PickupDeadlineUtc);
        Assert.NotNull(stored.CancelledAtUtc);
        Assert.Null(stored.CancelledByAdminAccountId);
        Assert.Equal(BookCopyStatus.Available, (await db.BookCopies.AsNoTracking().SingleAsync()).Status);
        var history = await db.BookCopyStatusHistories.AsNoTracking().SingleAsync();
        Assert.Equal(BookCopyStatus.OnHold, history.FromStatus);
        Assert.Equal(BookCopyStatus.Available, history.ToStatus);
    }

    [Fact]
    public async Task ReleasedCopyIsHeldForTheNextReaderInQueue()
    {
        await new WorkingScheduleService(db).GetWeeklySchedulesAsync();
        var (book, copy) = await AddBookWithCopyAsync();
        var overdue = await AddReadyHoldAsync(book, copy, DateTime.UtcNow.AddMinutes(-1));
        var next = await AddWaitingHoldAsync(book, DateTime.UtcNow.AddMinutes(1));

        await service.ExpireOverdueAsync(DateTime.UtcNow);

        var nextStored = await db.BookHolds.AsNoTracking().SingleAsync(item => item.Id == next.Id);
        Assert.Equal(BookHoldStatus.Available, nextStored.Status);
        Assert.Equal(copy.Id, nextStored.BookCopyId);
        Assert.NotNull(nextStored.PickupDeadlineUtc);
        Assert.True(nextStored.PickupDeadlineUtc > DateTime.UtcNow);
        Assert.Equal(BookCopyStatus.OnHold, (await db.BookCopies.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(BookHoldStatus.Cancelled, (await db.BookHolds.AsNoTracking().SingleAsync(item => item.Id == overdue.Id)).Status);
    }

    [Fact]
    public async Task HoldsStillWithinDeadlineAreNotTouched()
    {
        var (book, copy) = await AddBookWithCopyAsync();
        var hold = await AddReadyHoldAsync(book, copy, DateTime.UtcNow.AddHours(5));

        Assert.Equal(0, await service.ExpireOverdueAsync(DateTime.UtcNow));

        var stored = await db.BookHolds.AsNoTracking().SingleAsync(item => item.Id == hold.Id);
        Assert.Equal(BookHoldStatus.Available, stored.Status);
        Assert.Equal(copy.Id, stored.BookCopyId);
        Assert.Equal(BookCopyStatus.OnHold, (await db.BookCopies.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task ManualRunReportsCancelledAndReleasedCopyCounts()
    {
        var (book, copy) = await AddBookWithCopyAsync();
        await AddReadyHoldAsync(book, copy, DateTime.UtcNow.AddMinutes(-1));

        var result = await service.ProcessOverdueAsync(DateTime.UtcNow);

        Assert.Equal(1, result.CheckedCount);
        Assert.Equal(1, result.CancelledCount);
        Assert.Equal(0, result.AssignedToNextReaderCount);
        Assert.Equal(1, result.ReturnedToAvailableCount);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ExpiredHoldsAreRemovedBeforeReleasedCopiesAreAssigned()
    {
        var (book, firstCopy) = await AddBookWithCopyAsync();
        var secondCopy = new BookCopy
        {
            BookId = book.Id,
            Shelf = new Shelf
            {
                Warehouse = new Warehouse { Code = "WH-" + Guid.NewGuid().ToString("N")[..6], Name = "Kho" },
                Code = Guid.NewGuid().ToString("N"), Name = "Kệ"
            },
            CopyCode = "COPY-" + Guid.NewGuid().ToString("N")[..8],
            Status = BookCopyStatus.OnHold
        };
        db.BookCopies.Add(secondCopy);
        await db.SaveChangesAsync();
        var firstExpired = await AddReadyHoldAsync(book, firstCopy, DateTime.UtcNow.AddMinutes(-2));
        var secondExpired = await AddReadyHoldAsync(book, secondCopy, DateTime.UtcNow.AddMinutes(-1));
        var next = await AddWaitingHoldAsync(book, DateTime.UtcNow);

        var result = await service.ProcessOverdueAsync(DateTime.UtcNow);

        Assert.Equal(2, result.CancelledCount);
        Assert.Equal(BookHoldStatus.Cancelled, (await db.BookHolds.AsNoTracking().SingleAsync(item => item.Id == firstExpired.Id)).Status);
        Assert.Equal(BookHoldStatus.Cancelled, (await db.BookHolds.AsNoTracking().SingleAsync(item => item.Id == secondExpired.Id)).Status);
        Assert.Equal(BookHoldStatus.Available, (await db.BookHolds.AsNoTracking().SingleAsync(item => item.Id == next.Id)).Status);
        Assert.Equal(1, result.AssignedToNextReaderCount);
        Assert.Equal(1, result.ReturnedToAvailableCount);
    }

    [Theory]
    [InlineData(BookHoldStatus.Waiting)]
    [InlineData(BookHoldStatus.Cancelled)]
    [InlineData(BookHoldStatus.ConvertedToLoan)]
    public async Task OnlyHoldsWaitingForPickupCanExpire(string status)
    {
        var (book, _) = await AddBookWithCopyAsync();
        var hold = await AddWaitingHoldAsync(book, DateTime.UtcNow.AddDays(-5));
        await db.BookHolds.Where(item => item.Id == hold.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.Status, status)
            .SetProperty(item => item.PickupDeadlineUtc, DateTime.UtcNow.AddDays(-1)));

        Assert.Equal(0, await service.ExpireOverdueAsync(DateTime.UtcNow));

        Assert.Equal(status, (await db.BookHolds.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task ExpiredHoldDisappearsFromWaitingPickupList()
    {
        var (book, copy) = await AddBookWithCopyAsync();
        await AddReadyHoldAsync(book, copy, DateTime.UtcNow.AddMinutes(-1));
        Assert.Single(await new HoldPickupService(db).GetWaitingPickupHoldsAsync());

        await service.ExpireOverdueAsync(DateTime.UtcNow);

        Assert.Empty(await new HoldPickupService(db).GetWaitingPickupHoldsAsync());
    }

    private async Task<(Book Book, BookCopy Copy)> AddBookWithCopyAsync()
    {
        var unique = Guid.NewGuid().ToString("N");
        var book = new Book { Title = "Book " + unique, Author = new Author { Name = "Author " + unique } };
        var shelf = new Shelf { Warehouse = new Warehouse { Code = "WH-" + unique[..6], Name = "Kho" }, Code = unique, Name = "Kệ" };
        var copy = new BookCopy { Book = book, Shelf = shelf, CopyCode = "COPY-" + unique[..6], Status = BookCopyStatus.OnHold };
        db.BookCopies.Add(copy);
        await db.SaveChangesAsync();
        return (book, copy);
    }

    private async Task<BookHold> AddReadyHoldAsync(Book book, BookCopy copy, DateTime deadlineUtc)
    {
        var hold = new BookHold
        {
            BookId = book.Id, ReaderAccount = NewReader(), Status = BookHoldStatus.Available,
            BookCopyId = copy.Id, PickupDeadlineUtc = deadlineUtc, HeldAtUtc = DateTime.UtcNow.AddDays(-4)
        };
        db.BookHolds.Add(hold);
        await db.SaveChangesAsync();
        return hold;
    }

    private async Task<BookHold> AddWaitingHoldAsync(Book book, DateTime heldAtUtc)
    {
        var hold = new BookHold { BookId = book.Id, ReaderAccount = NewReader(), Status = BookHoldStatus.Waiting, HeldAtUtc = heldAtUtc };
        db.BookHolds.Add(hold);
        await db.SaveChangesAsync();
        return hold;
    }

    private static ReaderAccount NewReader() => new()
    {
        FullName = "Bạn đọc", DateOfBirth = new DateOnly(2000, 1, 1), Email = Guid.NewGuid() + "@example.com",
        PhoneNumber = "0900000000", StudentOrStaffCode = Guid.NewGuid().ToString(), PasswordHash = "hash", Status = "Đang hoạt động"
    };

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }
}
