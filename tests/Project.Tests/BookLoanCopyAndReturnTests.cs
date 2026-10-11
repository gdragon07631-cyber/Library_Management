using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class BookLoanCopyAndReturnTests
{
    [Fact]
    public async Task Create_WithAvailableCopy_AssignsItAndMarksItBorrowed()
    {
        await using var db = CreateDb();
        var (book, reader, copy) = await AddBorrowingDataAsync(db);
        var service = CreateService(db);

        var result = await service.CreateAsync(book.Id, reader.Id, DateOnly.FromDateTime(DateTime.Today), bookCopyId: copy.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(copy.Id, result.Loan!.BookCopyId);
        Assert.Equal(BookCopyStatuses.Borrowed, copy.Status);
        Assert.Null(result.Loan.ReturnedAtUtc);
    }

    [Fact]
    public async Task Create_WithCopyFromDifferentBook_IsRejectedWithoutChanges()
    {
        await using var db = CreateDb();
        var (book, reader, copy) = await AddBorrowingDataAsync(db);
        var otherBook = new Book { Title = "Other book", AuthorId = 1 };
        db.Books.Add(otherBook);
        await db.SaveChangesAsync();

        var result = await CreateService(db).CreateAsync(
            otherBook.Id, reader.Id, DateOnly.FromDateTime(DateTime.Today), bookCopyId: copy.Id);

        Assert.False(result.IsSuccess);
        Assert.Empty(await db.BookLoans.ToListAsync());
        Assert.Equal(BookCopyStatuses.Available, copy.Status);
    }

    [Fact]
    public async Task Create_WithLegacyUnassignedOpenLoan_DoesNotGuessOrAssignCopy()
    {
        await using var db = CreateDb();
        var (book, reader, copy) = await AddBorrowingDataAsync(db);
        db.BookLoans.Add(new BookLoan
        {
            BookId = book.Id, ReaderAccountId = reader.Id,
            LoanDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-1)),
            OriginalDueDate = DateOnly.FromDateTime(DateTime.Today.AddDays(13)),
            DueDate = DateOnly.FromDateTime(DateTime.Today.AddDays(13))
        });
        await db.SaveChangesAsync();

        var result = await CreateService(db).CreateAsync(
            book.Id, reader.Id, DateOnly.FromDateTime(DateTime.Today), bookCopyId: copy.Id);

        Assert.False(result.IsSuccess);
        Assert.Single(await db.BookLoans.ToListAsync());
        Assert.Null((await db.BookLoans.SingleAsync()).BookCopyId);
        Assert.Equal(BookCopyStatuses.Available, copy.Status);
    }

    [Fact]
    public async Task Return_LinkedLoan_ClosesItAndMakesCopyAvailable()
    {
        await using var db = CreateDb();
        var (book, reader, copy) = await AddBorrowingDataAsync(db);
        var service = CreateService(db);
        var created = await service.CreateAsync(book.Id, reader.Id, DateOnly.FromDateTime(DateTime.Today), bookCopyId: copy.Id);
        var returnedAt = DateTime.UtcNow;

        var result = await service.ReturnAsync(created.Loan!.Id, returnedAt);

        Assert.True(result.IsSuccess);
        Assert.Equal(returnedAt, result.Loan!.ReturnedAtUtc);
        Assert.Equal(BookCopyStatuses.Available, copy.Status);
        Assert.False((await service.RenewAsync(result.Loan.Id, DateOnly.FromDateTime(DateTime.Today))).IsSuccess);
        Assert.False((await service.ReturnAsync(result.Loan.Id, returnedAt.AddMinutes(1))).IsSuccess);

        var reloan = await service.CreateAsync(book.Id, reader.Id, DateOnly.FromDateTime(DateTime.Today), bookCopyId: copy.Id);
        Assert.True(reloan.IsSuccess);
        Assert.Equal(copy.Id, reloan.Loan!.BookCopyId);
        Assert.Equal(BookCopyStatuses.Borrowed, copy.Status);
    }

    [Fact]
    public async Task Return_LegacyLoan_RecordsReturnWithoutGuessingCopy()
    {
        await using var db = CreateDb();
        var (book, reader, _) = await AddBorrowingDataAsync(db);
        var legacyLoan = new BookLoan
        {
            BookId = book.Id, ReaderAccountId = reader.Id,
            LoanDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-1)),
            OriginalDueDate = DateOnly.FromDateTime(DateTime.Today.AddDays(13)),
            DueDate = DateOnly.FromDateTime(DateTime.Today.AddDays(13))
        };
        db.BookLoans.Add(legacyLoan);
        await db.SaveChangesAsync();

        var result = await CreateService(db).ReturnAsync(legacyLoan.Id, DateTime.UtcNow);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Loan!.BookCopyId);
        Assert.NotNull(result.Loan.ReturnedAtUtc);
    }

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new ApplicationDbContext(options);
    }

    private static BookLoanService CreateService(ApplicationDbContext db) =>
        new(db, new WorkingScheduleService(db));

    private static async Task<(Book Book, ReaderAccount Reader, BookCopy Copy)> AddBorrowingDataAsync(
        ApplicationDbContext db)
    {
        var book = new Book { Title = "Test book", AuthorId = 1 };
        var reader = new ReaderAccount
        {
            FullName = "Test Reader", DateOfBirth = new DateOnly(2000, 1, 1),
            Email = $"{Guid.NewGuid()}@example.test", PhoneNumber = "0900000000",
            StudentOrStaffCode = Guid.NewGuid().ToString("N"), PasswordHash = "hash", Status = "Đang hoạt động"
        };
        db.Books.Add(book);
        db.ReaderAccounts.Add(reader);
        await db.SaveChangesAsync();

        var copy = new BookCopy
        {
            BookId = book.Id, ShelfId = 1, CopyCode = $"COPY-{Guid.NewGuid():N}",
            Status = BookCopyStatuses.Available
        };
        db.BookCopies.Add(copy);
        await db.SaveChangesAsync();
        return (book, reader, copy);
    }
}
