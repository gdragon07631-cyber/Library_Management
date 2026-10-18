using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class StaffHoldCancellationTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;
    private readonly StaffHoldCancellationService service;

    public StaffHoldCancellationTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        service = new StaffHoldCancellationService(db);
    }

    [Fact]
    public async Task ValidReasonCancelsHoldAndStoresTrimmedReasonAndAuditData()
    {
        var hold = await AddHoldAsync(BookHoldStatus.Waiting);

        var result = await service.CancelAsync(hold.Id, "  Bạn đọc yêu cầu hủy.  ", 42);
        var saved = await db.BookHolds.SingleAsync(item => item.Id == hold.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(BookHoldStatus.Cancelled, saved.Status);
        Assert.Equal("Bạn đọc yêu cầu hủy.", saved.CancellationReason);
        Assert.NotNull(saved.CancelledAtUtc);
        Assert.Equal(42, saved.CancelledByAdminAccountId);
        Assert.Empty(await db.EmailSendHistories.ToListAsync());
    }

    [Fact]
    public async Task MissingReasonDoesNotChangeHold()
    {
        var hold = await AddHoldAsync(BookHoldStatus.Waiting);

        var result = await service.CancelAsync(hold.Id, null, 1);
        var saved = await db.BookHolds.SingleAsync(item => item.Id == hold.Id);

        Assert.Equal(StaffHoldCancellationResult.MissingReason, result.Result);
        Assert.Equal(BookHoldStatus.Waiting, saved.Status);
        Assert.Null(saved.CancellationReason);
    }

    [Fact]
    public async Task WhitespaceOnlyReasonDoesNotChangeHold()
    {
        var hold = await AddHoldAsync(BookHoldStatus.Available);

        var result = await service.CancelAsync(hold.Id, " \r\n\t ", 1);
        var saved = await db.BookHolds.SingleAsync(item => item.Id == hold.Id);

        Assert.Equal(StaffHoldCancellationResult.MissingReason, result.Result);
        Assert.Equal(BookHoldStatus.Available, saved.Status);
        Assert.Null(saved.CancellationReason);
    }

    [Fact]
    public async Task CancelledAndConvertedToLoanHoldsCannotBeCancelledAgain()
    {
        var cancelled = await AddHoldAsync(BookHoldStatus.Cancelled);
        var loaned = await AddHoldAsync(BookHoldStatus.ConvertedToLoan);

        Assert.Equal(StaffHoldCancellationResult.AlreadyCancelled, (await service.CancelAsync(cancelled.Id, "Lý do", 1)).Result);
        Assert.Equal(StaffHoldCancellationResult.ConvertedToLoan, (await service.CancelAsync(loaned.Id, "Lý do", 1)).Result);
        Assert.Equal(BookHoldStatus.Cancelled, (await db.BookHolds.SingleAsync(item => item.Id == cancelled.Id)).Status);
        Assert.Equal(BookHoldStatus.ConvertedToLoan, (await db.BookHolds.SingleAsync(item => item.Id == loaned.Id)).Status);
    }

    [Fact]
    public async Task SavedCancellationReasonIsReturnedByQueueDetails()
    {
        var hold = await AddHoldAsync(BookHoldStatus.Waiting);
        await service.CancelAsync(hold.Id, "Không còn nhu cầu", 1);
        var queueService = new BookHoldQueueService(db);

        var item = Assert.Single(await queueService.GetQueueForBookAsync(hold.BookId, BookHoldQueueFilter.Cancelled));

        Assert.Equal(BookHoldStatus.Cancelled, item.Status);
        Assert.Equal("Không còn nhu cầu", item.CancellationReason);
    }

    private async Task<BookHold> AddHoldAsync(string status)
    {
        var author = new Author { Name = Guid.NewGuid().ToString() };
        var book = new Book { Title = Guid.NewGuid().ToString(), Author = author };
        var reader = new ReaderAccount
        {
            FullName = "Bạn đọc", DateOfBirth = new DateOnly(2000, 1, 1), Email = Guid.NewGuid() + "@example.com",
            PhoneNumber = "0900000000", StudentOrStaffCode = Guid.NewGuid().ToString(), PasswordHash = "hash", Status = "Đang hoạt động"
        };
        var hold = new BookHold { Book = book, ReaderAccount = reader, Status = status, HeldAtUtc = DateTime.UtcNow };
        db.BookHolds.Add(hold);
        await db.SaveChangesAsync();
        return hold;
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }
}
