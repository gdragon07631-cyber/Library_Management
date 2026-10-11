using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Project.Controllers;
using Project.Data;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class HoldPickupExpiryHistoryTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;

    public HoldPickupExpiryHistoryTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task ListsOnlyAutomaticallyExpiredHoldsInsideInclusiveThirtyDayUtcWindow()
    {
        var now = new DateTime(2026, 10, 31, 17, 30, 0, DateTimeKind.Utc);
        var boundary = now.AddDays(-30);
        var book = new Book { Title = "Số đỏ", Author = new Author { Name = "Vũ Trọng Phụng" } };
        var reader = NewReader("Bạn đọc A");
        db.Books.Add(book);
        db.ReaderAccounts.Add(reader);
        await db.SaveChangesAsync();

        var atBoundary = AddHold(book, reader, BookHoldStatus.Cancelled, HoldPickupExpiryService.ExpiredReason,
            boundary, outcome: BookHoldExpiryCopyOutcome.TransferredToNextReader, copyCode: "COPY-01", nextHoldId: 777);
        var recentReady = AddHold(book, reader, BookHoldStatus.Cancelled, HoldPickupExpiryService.ExpiredReason,
            now.AddHours(-1), outcome: BookHoldExpiryCopyOutcome.ReturnedToAvailable, copyCode: "COPY-02");
        db.BookHolds.AddRange(atBoundary, recentReady,
            AddHold(book, reader, BookHoldStatus.Cancelled, HoldPickupExpiryService.ExpiredReason, boundary.AddTicks(-1)),
            AddHold(book, reader, BookHoldStatus.Cancelled, "Reader cancelled", now.AddHours(-2), cancelledBy: 3),
            AddHold(book, reader, BookHoldStatus.Cancelled, "Other cancellation reason", now.AddHours(-3)),
            AddHold(book, reader, BookHoldStatus.Available, HoldPickupExpiryService.ExpiredReason, now.AddMinutes(-5)),
            AddHold(book, reader, BookHoldStatus.Cancelled, HoldPickupExpiryService.ExpiredReason, now.AddMinutes(1)));
        await db.SaveChangesAsync();

        var page = await new HoldPickupExpiryHistoryService(db).GetLast30DaysAsync(now, pageSize: 1);

        Assert.Equal(2, page.TotalCount);
        var item = Assert.Single(page.Items);
        Assert.Equal(recentReady.Id, item.HoldId);
        Assert.Equal("Số đỏ", item.BookTitle);
        Assert.Equal("Bạn đọc A", item.ReaderName);
        Assert.Equal("COPY-02", item.ExpiredBookCopyCode);
        Assert.Equal(HoldPickupExpiryService.ExpiredReason, item.CancellationReason);
        Assert.Equal(BookHoldExpiryCopyOutcome.ReturnedToAvailable, item.ExpiryCopyOutcome);

        var secondPage = await new HoldPickupExpiryHistoryService(db).GetLast30DaysAsync(now, page: 2, pageSize: 1);
        var boundaryItem = Assert.Single(secondPage.Items);
        Assert.Equal(atBoundary.Id, boundaryItem.HoldId);
        Assert.Equal(BookHoldExpiryCopyOutcome.TransferredToNextReader, boundaryItem.ExpiryCopyOutcome);
        Assert.Equal(777, boundaryItem.ExpiryNextHoldId);
    }

    [Fact]
    public async Task OlderRowsWithoutStoredCopyOutcomeRemainUnknownInsteadOfBeingInferred()
    {
        var now = DateTime.UtcNow;
        var book = new Book { Title = "Book", Author = new Author { Name = "Author" } };
        var reader = NewReader("Reader");
        db.BookHolds.Add(AddHold(book, reader, BookHoldStatus.Cancelled,
            HoldPickupExpiryService.ExpiredReason, now.AddDays(-1)));
        await db.SaveChangesAsync();

        var page = await new HoldPickupExpiryHistoryService(db).GetLast30DaysAsync(now);

        Assert.Null(Assert.Single(page.Items).ExpiryCopyOutcome);
    }

    [Fact]
    public void HistoryControllerRequiresManagementRole()
    {
        var authorization = Assert.Single(typeof(HoldPickupHistoryController)
            .GetCustomAttributes(typeof(StaffOnlyAttribute), inherit: true).Cast<StaffOnlyAttribute>());

        Assert.Contains(AccountRoles.LibraryManager, authorization.Roles);
        Assert.Contains(AccountRoles.SystemAdmin, authorization.Roles);
        Assert.DoesNotContain(AccountRoles.Librarian, authorization.Roles);
    }

    private BookHold AddHold(Book book, ReaderAccount reader, string status, string reason,
        DateTime? cancelledAt, string? outcome = null, string? copyCode = null, long? nextHoldId = null,
        int? cancelledBy = null) => new()
    {
        Book = book,
        ReaderAccount = reader,
        Status = status,
        CancellationReason = reason,
        CancelledAtUtc = cancelledAt,
        CancelledByAdminAccountId = cancelledBy,
        ExpiredBookCopyCode = copyCode,
        ExpiryCopyOutcome = outcome,
        ExpiryNextHoldId = nextHoldId,
        HeldAtUtc = DateTime.UtcNow.AddDays(-31)
    };

    private static ReaderAccount NewReader(string name) => new()
    {
        FullName = name,
        DateOfBirth = new DateOnly(2000, 1, 1),
        Email = Guid.NewGuid() + "@example.com",
        PhoneNumber = "0900000000",
        StudentOrStaffCode = Guid.NewGuid().ToString(),
        PasswordHash = "hash",
        Status = "Active"
    };

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }
}
