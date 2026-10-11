using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Project.Controllers;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class BookCopyLoanLookupTests
{
    [Fact]
    public async Task Lookup_OpenLoanOnDueDate_ReturnsReaderAndZeroOverdueDays()
    {
        await using var db = CreateDb();
        var today = new DateOnly(2026, 10, 11);
        var (copy, reader) = await AddCopyAndReaderAsync(db);
        await AddLoanAsync(db, copy, reader, today);

        var result = await CreateService(db).LookupByCopyCodeAsync(copy.CopyCode, today);

        Assert.Equal(BookCopyLoanLookupStatus.Borrowed, result.Status);
        Assert.Equal(copy.CopyCode, result.CopyCode);
        Assert.Equal(reader.FullName, result.ReaderName);
        Assert.Equal(today, result.DueDate);
        Assert.Equal(0, result.OverdueDays);
    }

    [Fact]
    public async Task Lookup_OpenLoanBeforeDueDate_ReturnsZeroOverdueDays()
    {
        await using var db = CreateDb();
        var today = new DateOnly(2026, 10, 10);
        var (copy, reader) = await AddCopyAndReaderAsync(db);
        await AddLoanAsync(db, copy, reader, today.AddDays(3));

        var result = await CreateService(db).LookupByCopyCodeAsync(copy.CopyCode, today);

        Assert.Equal(BookCopyLoanLookupStatus.Borrowed, result.Status);
        Assert.Equal(today.AddDays(3), result.DueDate);
        Assert.Equal(0, result.OverdueDays);
    }

    [Fact]
    public async Task Lookup_OverdueLoan_UsesCalendarDayDifference()
    {
        await using var db = CreateDb();
        var today = new DateOnly(2026, 10, 13);
        var dueDate = new DateOnly(2026, 10, 10);
        var (copy, reader) = await AddCopyAndReaderAsync(db);
        await AddLoanAsync(db, copy, reader, dueDate);

        var result = await CreateService(db).LookupByCopyCodeAsync(copy.CopyCode, today);

        Assert.Equal(BookCopyLoanLookupStatus.Borrowed, result.Status);
        Assert.Equal(dueDate, result.DueDate);
        Assert.Equal(3, result.OverdueDays);
    }

    [Fact]
    public async Task Lookup_ExistingCopyWithoutOpenLoan_ReturnsNotBorrowed()
    {
        await using var db = CreateDb();
        var (copy, _) = await AddCopyAndReaderAsync(db);

        var result = await CreateService(db).LookupByCopyCodeAsync(copy.CopyCode, new DateOnly(2026, 10, 11));

        Assert.Equal(BookCopyLoanLookupStatus.NotBorrowed, result.Status);
    }

    [Fact]
    public async Task Lookup_UnknownBarcode_ReturnsCopyNotFound()
    {
        await using var db = CreateDb();
        var (existingCopy, reader) = await AddCopyAndReaderAsync(db);
        await AddLoanAsync(db, existingCopy, reader, new DateOnly(2026, 10, 11));

        var result = await CreateService(db).LookupByCopyCodeAsync("UNKNOWN", new DateOnly(2026, 10, 11));

        Assert.Equal(BookCopyLoanLookupStatus.CopyNotFound, result.Status);
        Assert.Null(result.ReaderName);
    }

    [Fact]
    public async Task LookupApi_BlankBarcode_IsRejectedWithoutQuerying()
    {
        await using var db = CreateDb();
        var controller = new LoanController(CreateService(db), db, NullLogger<LoanController>.Instance);

        var result = await controller.LookupByBarcodeApi("  ");

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.BookCopies.ToListAsync());
    }

    [Fact]
    public async Task LookupApi_ServiceFailure_ReturnsGenericServerError()
    {
        await using var db = CreateDb();
        var controller = new LoanController(new FailingLookupService(), db, NullLogger<LoanController>.Instance);

        var result = await controller.LookupByBarcodeApi("COPY-1");

        var serverError = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, serverError.StatusCode);
        Assert.DoesNotContain("private database detail", System.Text.Json.JsonSerializer.Serialize(serverError.Value));
    }

    [Fact]
    public async Task Lookup_OnlyReturnedLoan_ReturnsNotBorrowed()
    {
        await using var db = CreateDb();
        var (copy, reader) = await AddCopyAndReaderAsync(db);
        var returnedLoan = await AddLoanAsync(db, copy, reader, new DateOnly(2026, 10, 10));
        returnedLoan.ReturnedAtUtc = new DateTime(2026, 10, 10, 10, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();

        var result = await CreateService(db).LookupByCopyCodeAsync(copy.CopyCode, new DateOnly(2026, 10, 11));

        Assert.Equal(BookCopyLoanLookupStatus.NotBorrowed, result.Status);
    }

    [Fact]
    public async Task Lookup_UnassignedOpenLoanForSameBook_IsReportedAsAmbiguous()
    {
        await using var db = CreateDb();
        var today = new DateOnly(2026, 10, 11);
        var (copy, reader) = await AddCopyAndReaderAsync(db);
        db.BookLoans.Add(new BookLoan
        {
            BookId = copy.BookId, ReaderAccountId = reader.Id,
            LoanDate = today.AddDays(-1), OriginalDueDate = today.AddDays(13), DueDate = today.AddDays(13)
        });
        await db.SaveChangesAsync();

        var result = await CreateService(db).LookupByCopyCodeAsync(copy.CopyCode, today);

        Assert.Equal(BookCopyLoanLookupStatus.UnassignedOpenLoan, result.Status);
        Assert.Null(result.ReaderName);
    }

    [Fact]
    public async Task Lookup_MultipleOpenLoansForSameCopy_ReturnsInconsistentInsteadOfWrongReader()
    {
        await using var db = CreateDb();
        var today = new DateOnly(2026, 10, 11);
        var (copy, reader) = await AddCopyAndReaderAsync(db);
        await AddLoanAsync(db, copy, reader, today);
        await AddLoanAsync(db, copy, reader, today.AddDays(1));

        var result = await CreateService(db).LookupByCopyCodeAsync(copy.CopyCode, today);

        Assert.Equal(BookCopyLoanLookupStatus.Inconsistent, result.Status);
        Assert.Null(result.ReaderName);
    }

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new ApplicationDbContext(options);
    }

    private static BookLoanService CreateService(ApplicationDbContext db) =>
        new(db, new WorkingScheduleService(db));

    private static async Task<(BookCopy Copy, ReaderAccount Reader)> AddCopyAndReaderAsync(ApplicationDbContext db)
    {
        var book = new Book { Title = "Lookup test book", AuthorId = 1 };
        var reader = new ReaderAccount
        {
            FullName = "Lookup Test Reader", DateOfBirth = new DateOnly(2000, 1, 1),
            Email = $"{Guid.NewGuid()}@example.test", PhoneNumber = "0900000000",
            StudentOrStaffCode = Guid.NewGuid().ToString("N"), PasswordHash = "hash", Status = "Đang hoạt động"
        };
        db.Books.Add(book);
        db.ReaderAccounts.Add(reader);
        await db.SaveChangesAsync();

        var copy = new BookCopy
        {
            BookId = book.Id, ShelfId = 1, CopyCode = $"LOOKUP-{Guid.NewGuid():N}",
            Status = BookCopyStatuses.Borrowed
        };
        db.BookCopies.Add(copy);
        await db.SaveChangesAsync();
        return (copy, reader);
    }

    private static async Task<BookLoan> AddLoanAsync(
        ApplicationDbContext db, BookCopy copy, ReaderAccount reader, DateOnly dueDate)
    {
        var loan = new BookLoan
        {
            BookId = copy.BookId, BookCopyId = copy.Id, ReaderAccountId = reader.Id,
            LoanDate = dueDate.AddDays(-14), OriginalDueDate = dueDate, DueDate = dueDate
        };
        db.BookLoans.Add(loan);
        await db.SaveChangesAsync();
        return loan;
    }

    private sealed class FailingLookupService : IBookLoanService
    {
        public Task<BookCopyLoanLookupOutcome> LookupByCopyCodeAsync(string copyCode, DateOnly today, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("private database detail");

        public Task<IReadOnlyList<BookLoan>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BookLoanOutcome> CreateAsync(int bookId, int readerAccountId, DateOnly loanDate, CancellationToken cancellationToken = default, long? bookCopyId = null) => throw new NotImplementedException();
        public Task<RenewBookLoanOutcome> RenewAsync(long loanId, DateOnly today, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BookLoanOutcome> ReturnAsync(long loanId, DateTime returnedAtUtc, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<DateOnly> AdjustDueDateAsync(DateOnly proposedDate, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
