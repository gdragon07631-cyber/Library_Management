using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class BookLoanReturnTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(-3)]
    public async Task Return_OpenLoan_RecordsActualTimeAndUpdatesOnlyItsCopy(int dueDateOffset)
    {
        await using var db = CreateInMemoryDb();
        var (loan, copy, otherCopy) = await SeedLoanAsync(db, dueDateOffset);
        var returnedAt = DateTime.UtcNow;
        var result = await CreateService(db).ReturnAsync(loan.Id, returnedAt);

        Assert.True(result.IsSuccess);
        Assert.Equal(returnedAt, (await db.BookLoans.AsNoTracking().SingleAsync()).ReturnedAtUtc);
        Assert.Equal(BookCopyStatuses.Available, (await db.BookCopies.AsNoTracking().SingleAsync(item => item.Id == copy.Id)).Status);
        Assert.Equal(BookCopyStatuses.Borrowed, (await db.BookCopies.AsNoTracking().SingleAsync(item => item.Id == otherCopy.Id)).Status);
        Assert.Equal(BookCopyLoanLookupStatus.NotBorrowed,
            (await CreateService(db).LookupByCopyCodeAsync(copy.CopyCode, DateOnly.FromDateTime(DateTime.Today))).Status);
    }

    [Fact]
    public async Task Return_AlreadyReturnedLoan_DoesNotOverwriteDateOrCopyState()
    {
        await using var db = CreateInMemoryDb();
        var (loan, copy, _) = await SeedLoanAsync(db, 3);
        var originalReturn = DateTime.UtcNow.AddDays(-1);
        loan.ReturnedAtUtc = originalReturn;
        copy.Status = BookCopyStatuses.Available;
        await db.SaveChangesAsync();

        var result = await CreateService(db).ReturnAsync(loan.Id, DateTime.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsConflict);
        Assert.Equal(originalReturn, (await db.BookLoans.AsNoTracking().SingleAsync(item => item.Id == loan.Id)).ReturnedAtUtc);
        Assert.Equal(BookCopyStatuses.Available, (await db.BookCopies.AsNoTracking().SingleAsync(item => item.Id == copy.Id)).Status);
    }

    [Fact]
    public async Task Return_LegacyLoanWithoutCopy_IsRejectedWithoutGuessing()
    {
        await using var db = CreateInMemoryDb();
        var (loan, copy, _) = await SeedLoanAsync(db, 3);
        loan.BookCopyId = null;
        loan.BookCopy = null;
        await db.SaveChangesAsync();

        var result = await CreateService(db).ReturnAsync(loan.Id, DateTime.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsConflict);
        Assert.Null((await db.BookLoans.AsNoTracking().SingleAsync()).ReturnedAtUtc);
        Assert.Equal(BookCopyStatuses.Borrowed, (await db.BookCopies.AsNoTracking().SingleAsync(item => item.Id == copy.Id)).Status);
    }

    [Fact]
    public async Task Return_RepeatedRequestsOnlySucceedOnce()
    {
        await using var db = CreateInMemoryDb();
        var (loan, _, _) = await SeedLoanAsync(db, 3);
        var service = CreateService(db);

        var first = await service.ReturnAsync(loan.Id, DateTime.UtcNow);
        var second = await service.ReturnAsync(loan.Id, DateTime.UtcNow.AddSeconds(1));

        Assert.True(first.IsSuccess);
        Assert.False(second.IsSuccess);
        Assert.Equal(first.Loan!.ReturnedAtUtc, (await db.BookLoans.AsNoTracking().SingleAsync()).ReturnedAtUtc);
    }

    [Fact]
    public async Task Return_FailureUpdatingCopy_RollsBackLoanUpdate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var (loan, copy, _) = await SeedRelationalLoanAsync(db);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER FailBookCopyReturn BEFORE UPDATE OF Status ON BookCopies BEGIN SELECT RAISE(ABORT, 'copy update failed'); END;");

        await Assert.ThrowsAsync<SqliteException>(() => CreateService(db).ReturnAsync(loan.Id, DateTime.UtcNow));

        Assert.Null((await db.BookLoans.AsNoTracking().SingleAsync(item => item.Id == loan.Id)).ReturnedAtUtc);
        Assert.Equal(BookCopyStatuses.Borrowed,
            (await db.BookCopies.AsNoTracking().SingleAsync(item => item.Id == copy.Id)).Status);
    }

    private static ApplicationDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new ApplicationDbContext(options);
    }

    private static BookLoanService CreateService(ApplicationDbContext db) =>
        new(db, new WorkingScheduleService(db));

    private static async Task<(BookLoan Loan, BookCopy Copy, BookCopy OtherCopy)> SeedLoanAsync(
        ApplicationDbContext db, int dueDateOffset)
    {
        var book = new Book { Id = 1, Title = "Return test book", AuthorId = 1 };
        var reader = new ReaderAccount
        {
            Id = 1, FullName = "Return Test Reader", DateOfBirth = new DateOnly(2000, 1, 1),
            Email = "return-test@example.test", PhoneNumber = "0900000000", StudentOrStaffCode = "RETURN-1",
            PasswordHash = "hash", Status = "Đang hoạt động"
        };
        var copy = new BookCopy { Id = 1, BookId = book.Id, ShelfId = 1, CopyCode = "RETURN-COPY-1", Status = BookCopyStatuses.Borrowed };
        var otherCopy = new BookCopy { Id = 2, BookId = book.Id, ShelfId = 1, CopyCode = "RETURN-COPY-2", Status = BookCopyStatuses.Borrowed };
        var dueDate = DateOnly.FromDateTime(DateTime.Today).AddDays(dueDateOffset);
        var loan = new BookLoan
        {
            Id = 1, BookId = book.Id, Book = book, BookCopyId = copy.Id, BookCopy = copy,
            ReaderAccountId = reader.Id, ReaderAccount = reader,
            LoanDate = dueDate.AddDays(-14), OriginalDueDate = dueDate, DueDate = dueDate
        };
        db.AddRange(book, reader, copy, otherCopy, loan);
        await db.SaveChangesAsync();
        return (loan, copy, otherCopy);
    }

    private static async Task<(BookLoan Loan, BookCopy Copy, BookCopy OtherCopy)> SeedRelationalLoanAsync(ApplicationDbContext db)
    {
        var author = new Author { Name = "Return test author" };
        var warehouse = new Warehouse { Code = "RET", Name = "Return test warehouse" };
        var shelf = new Shelf { Code = "RET-1", Name = "Return test shelf", Warehouse = warehouse };
        var book = new Book { Title = "Return test book", Author = author };
        var reader = new ReaderAccount
        {
            FullName = "Return Test Reader", DateOfBirth = new DateOnly(2000, 1, 1),
            Email = "return-test@example.test", PhoneNumber = "0900000000", StudentOrStaffCode = "RETURN-1",
            PasswordHash = "hash", Status = "Đang hoạt động"
        };
        db.AddRange(author, warehouse, shelf, book, reader);
        await db.SaveChangesAsync();

        var copy = new BookCopy { BookId = book.Id, ShelfId = shelf.Id, CopyCode = "RETURN-COPY-1", Status = BookCopyStatuses.Borrowed };
        var otherCopy = new BookCopy { BookId = book.Id, ShelfId = shelf.Id, CopyCode = "RETURN-COPY-2", Status = BookCopyStatuses.Borrowed };
        db.BookCopies.AddRange(copy, otherCopy);
        await db.SaveChangesAsync();

        var dueDate = DateOnly.FromDateTime(DateTime.Today).AddDays(5);
        var loan = new BookLoan
        {
            BookId = book.Id, BookCopyId = copy.Id, ReaderAccountId = reader.Id,
            LoanDate = dueDate.AddDays(-14), OriginalDueDate = dueDate, DueDate = dueDate
        };
        db.BookLoans.Add(loan);
        await db.SaveChangesAsync();
        return (loan, copy, otherCopy);
    }
}
