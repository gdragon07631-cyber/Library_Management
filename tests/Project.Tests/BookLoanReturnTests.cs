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
    public async Task Return_WithPendingHold_ReservesReturnedCopyForThatReader()
    {
        await using var db = CreateInMemoryDb();
        var (loan, copy, _) = await SeedLoanAsync(db, 3);
        var waitingReader = CreateReader(2, "WAIT-1");
        var hold = new BookHold
        {
            Id = 1, BookId = loan.BookId, Book = loan.Book, ReaderAccountId = waitingReader.Id,
            ReaderAccount = waitingReader, HeldAtUtc = DateTime.UtcNow.AddHours(-1)
        };
        db.AddRange(waitingReader, hold);
        await db.SaveChangesAsync();

        var result = await CreateService(db).ReturnAsync(loan.Id, DateTime.UtcNow);

        Assert.True(result.IsSuccess);
        Assert.Equal(BookCopyStatuses.Held, result.CopyStatus);
        Assert.Equal(BookCopyStatuses.Held, (await db.BookCopies.FindAsync(copy.Id))!.Status);
        var savedHold = await db.BookHolds.AsNoTracking().SingleAsync();
        Assert.Equal(BookHoldStatuses.Assigned, savedHold.Status);
        Assert.Equal(copy.Id, savedHold.BookCopyId);
        Assert.Equal(waitingReader.Id, result.AssignedHold?.ReaderAccountId);
    }

    [Fact]
    public async Task Return_SelectsOldestEligibleHold_AndLeavesOtherQueueEntriesUntouched()
    {
        await using var db = CreateInMemoryDb();
        var (loan, copy, _) = await SeedLoanAsync(db, 3);
        var now = DateTime.UtcNow;
        var cancelledReader = CreateReader(2, "WAIT-CANCELLED");
        var inactiveReader = CreateReader(3, "WAIT-INACTIVE", "Ngưng hoạt động");
        var firstReader = CreateReader(4, "WAIT-FIRST");
        var secondReader = CreateReader(5, "WAIT-SECOND");
        var cancelled = new BookHold
        {
            Id = 1, BookId = loan.BookId, ReaderAccountId = cancelledReader.Id, ReaderAccount = cancelledReader,
            HeldAtUtc = now.AddHours(-4), Status = BookHoldStatuses.Cancelled
        };
        var inactive = new BookHold
        {
            Id = 2, BookId = loan.BookId, ReaderAccountId = inactiveReader.Id, ReaderAccount = inactiveReader,
            HeldAtUtc = now.AddHours(-3)
        };
        var first = new BookHold
        {
            Id = 3, BookId = loan.BookId, ReaderAccountId = firstReader.Id, ReaderAccount = firstReader,
            HeldAtUtc = now.AddHours(-2)
        };
        var second = new BookHold
        {
            Id = 4, BookId = loan.BookId, ReaderAccountId = secondReader.Id, ReaderAccount = secondReader,
            HeldAtUtc = now.AddHours(-1)
        };
        db.AddRange(cancelledReader, inactiveReader, firstReader, secondReader, cancelled, inactive, first, second);
        await db.SaveChangesAsync();

        var result = await CreateService(db).ReturnAsync(loan.Id, now);

        Assert.True(result.IsSuccess);
        Assert.Equal(firstReader.Id, result.AssignedHold?.ReaderAccountId);
        Assert.Equal(BookCopyStatuses.Held, (await db.BookCopies.FindAsync(copy.Id))!.Status);
        Assert.Equal(BookHoldStatuses.Cancelled, (await db.BookHolds.FindAsync(cancelled.Id))!.Status);
        Assert.Equal(BookHoldStatuses.Pending, (await db.BookHolds.FindAsync(inactive.Id))!.Status);
        Assert.Null((await db.BookHolds.FindAsync(inactive.Id))!.BookCopyId);
        Assert.Equal(BookHoldStatuses.Assigned, (await db.BookHolds.FindAsync(first.Id))!.Status);
        Assert.Equal(copy.Id, (await db.BookHolds.FindAsync(first.Id))!.BookCopyId);
        Assert.Equal(BookHoldStatuses.Pending, (await db.BookHolds.FindAsync(second.Id))!.Status);
        Assert.Null((await db.BookHolds.FindAsync(second.Id))!.BookCopyId);
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
        var waitingReader = CreateReader(2, "RETURN-WAITING");
        var hold = new BookHold
        {
            BookId = loan.BookId, ReaderAccountId = waitingReader.Id, HeldAtUtc = DateTime.UtcNow
        };
        db.AddRange(waitingReader, hold);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER FailBookCopyReturn BEFORE UPDATE OF Status ON BookCopies BEGIN SELECT RAISE(ABORT, 'copy update failed'); END;");

        await Assert.ThrowsAsync<SqliteException>(() => CreateService(db).ReturnAsync(loan.Id, DateTime.UtcNow));

        Assert.Null((await db.BookLoans.AsNoTracking().SingleAsync(item => item.Id == loan.Id)).ReturnedAtUtc);
        Assert.Equal(BookCopyStatuses.Borrowed,
            (await db.BookCopies.AsNoTracking().SingleAsync(item => item.Id == copy.Id)).Status);
        var savedHold = await db.BookHolds.AsNoTracking().SingleAsync();
        Assert.Equal(BookHoldStatuses.Pending, savedHold.Status);
        Assert.Null(savedHold.BookCopyId);
    }

    [Fact]
    public async Task Return_RelationalDatabase_AssignsHoldAndUpdatesAllRecords()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var (loan, copy, _) = await SeedRelationalLoanAsync(db);
        var waitingReader = CreateReader(2, "RETURN-RELATIONAL-WAITING");
        var hold = new BookHold
        {
            BookId = loan.BookId, ReaderAccountId = waitingReader.Id, HeldAtUtc = DateTime.UtcNow
        };
        db.AddRange(waitingReader, hold);
        await db.SaveChangesAsync();

        var result = await CreateService(db).ReturnAsync(loan.Id, DateTime.UtcNow);

        Assert.True(result.IsSuccess);
        Assert.NotNull((await db.BookLoans.AsNoTracking().SingleAsync(item => item.Id == loan.Id)).ReturnedAtUtc);
        Assert.Equal(BookCopyStatuses.Held,
            (await db.BookCopies.AsNoTracking().SingleAsync(item => item.Id == copy.Id)).Status);
        var savedHold = await db.BookHolds.AsNoTracking().SingleAsync();
        Assert.Equal(BookHoldStatuses.Assigned, savedHold.Status);
        Assert.Equal(copy.Id, savedHold.BookCopyId);
    }

    private static ApplicationDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new ApplicationDbContext(options);
    }

    private static BookLoanService CreateService(ApplicationDbContext db) =>
        new(db, new WorkingScheduleService(db));

    private static ReaderAccount CreateReader(int id, string code, string status = "Đang hoạt động") => new()
    {
        Id = id, FullName = $"Reader {code}", DateOfBirth = new DateOnly(2000, 1, 1),
        Email = $"{code.ToLowerInvariant()}@example.test", PhoneNumber = "0900000000",
        StudentOrStaffCode = code, PasswordHash = "hash", Status = status
    };

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
