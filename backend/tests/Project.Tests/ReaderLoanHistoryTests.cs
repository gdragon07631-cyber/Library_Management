using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Project.Controllers;
using Project.Data;
using Project.Filters;
using Project.Models;

namespace Project.Tests;

public sealed class ReaderLoanHistoryTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;

    public ReaderLoanHistoryTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    [Fact]
    public async Task DetailsReturnsActualLoansNewestFirstAndZeroSafeSummary()
    {
        var reader = NewReader("history@example.com", "R-HISTORY");
        db.ReaderAccounts.Add(reader);
        var author = new Author { Name = "Test author" };
        db.Authors.Add(author);
        await db.SaveChangesAsync();
        var book = new Book { Title = "Test book", AuthorId = author.Id };
        db.Books.Add(book);
        await db.SaveChangesAsync();
        db.BookLoans.AddRange(
            new BookLoan { ReaderAccountId = reader.Id, BookId = book.Id, LoanDate = new DateOnly(2026, 1, 1), OriginalDueDate = new DateOnly(2026, 1, 15), DueDate = new DateOnly(2026, 1, 15) },
            new BookLoan { ReaderAccountId = reader.Id, BookId = book.Id, LoanDate = new DateOnly(2026, 2, 1), OriginalDueDate = new DateOnly(2026, 2, 15), DueDate = new DateOnly(2026, 2, 15) });
        await db.SaveChangesAsync();

        var result = Assert.IsType<ViewResult>(await new ReaderLoanHistoryController(db).Details(reader.Id));
        var model = Assert.IsType<ReaderLoanHistoryViewModel>(result.Model);
        Assert.Equal(2, model.TotalLoanCount);
        Assert.Equal(2, model.OpenLoanCount);
        Assert.Null(model.LateReturnCount);
        Assert.Equal([new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1)], model.Loans.Select(loan => loan.LoanDate));
    }

    [Fact]
    public async Task DetailsReturnsNotFoundForUnknownReaderAndEmptyHistoryIsZero()
    {
        var missing = await new ReaderLoanHistoryController(db).Details(999);
        Assert.IsType<NotFoundObjectResult>(missing);

        var reader = NewReader("empty@example.com", "R-EMPTY");
        db.ReaderAccounts.Add(reader);
        await db.SaveChangesAsync();
        var result = Assert.IsType<ViewResult>(await new ReaderLoanHistoryController(db).Details(reader.Id));
        var model = Assert.IsType<ReaderLoanHistoryViewModel>(result.Model);
        Assert.Empty(model.Loans);
        Assert.Equal(0, model.OpenLoanCount);
        Assert.Equal(0, model.TotalLoanCount);
    }

    [Fact]
    public async Task DateFiltersAreInclusiveAndRecalculateTheVisibleSummary()
    {
        var (reader, book) = await AddHistoryFixtureAsync();
        var dates = new[]
        {
            new DateOnly(2026, 5, 9),
            new DateOnly(2026, 5, 10),
            new DateOnly(2026, 5, 12),
            new DateOnly(2026, 5, 13)
        };
        foreach (var date in dates) await AddLoanAsync(reader, book, date);

        var controller = new ReaderLoanHistoryController(db);

        var fromOnly = await GetModelAsync(controller, reader.Id, new DateOnly(2026, 5, 10), null);
        Assert.Equal([dates[3], dates[2], dates[1]], fromOnly.Loans.Select(loan => loan.LoanDate));
        Assert.Equal(3, fromOnly.TotalLoanCount);

        var throughOnly = await GetModelAsync(controller, reader.Id, null, new DateOnly(2026, 5, 12));
        Assert.Equal([dates[2], dates[1], dates[0]], throughOnly.Loans.Select(loan => loan.LoanDate));
        Assert.Equal(3, throughOnly.TotalLoanCount);

        var range = await GetModelAsync(controller, reader.Id, dates[1], dates[2]);
        Assert.Equal([dates[2], dates[1]], range.Loans.Select(loan => loan.LoanDate));
        Assert.Equal(2, range.OpenLoanCount);
        Assert.Equal(2, range.TotalLoanCount);
        Assert.Null(range.LateReturnCount);

        var cleared = await GetModelAsync(controller, reader.Id, null, null);
        Assert.Equal([dates[3], dates[2], dates[1], dates[0]], cleared.Loans.Select(loan => loan.LoanDate));
        Assert.Equal(4, cleared.TotalLoanCount);
    }

    [Fact]
    public async Task EmptyRangeAndInvalidDateRangeAreHandledWithoutMisleadingRows()
    {
        var (reader, book) = await AddHistoryFixtureAsync();
        await AddLoanAsync(reader, book, new DateOnly(2026, 5, 10));
        var controller = new ReaderLoanHistoryController(db);

        var emptyRange = await GetModelAsync(controller, reader.Id, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
        Assert.Empty(emptyRange.Loans);
        Assert.Equal(0, emptyRange.OpenLoanCount);
        Assert.Equal(0, emptyRange.TotalLoanCount);
        Assert.Null(emptyRange.DateFilterError);

        var invalidRange = await GetModelAsync(controller, reader.Id, new DateOnly(2026, 6, 30), new DateOnly(2026, 6, 1));
        Assert.Empty(invalidRange.Loans);
        Assert.Equal(0, invalidRange.TotalLoanCount);
        Assert.Equal("Ngày bắt đầu không được sau ngày kết thúc.", invalidRange.DateFilterError);

        var malformedController = new ReaderLoanHistoryController(db);
        malformedController.ModelState.AddModelError("startDate", "Invalid date");
        var malformed = await GetModelAsync(malformedController, reader.Id, null, null);
        Assert.Empty(malformed.Loans);
        Assert.Equal("Ngày lọc không hợp lệ. Vui lòng chọn lại ngày theo định dạng hợp lệ.", malformed.DateFilterError);
    }

    [Fact]
    public void HistoryControllerIsRestrictedToLibrariansAndManagers()
    {
        var access = Assert.IsType<StaffOnlyAttribute>(Attribute.GetCustomAttribute(typeof(ReaderLoanHistoryController), typeof(StaffOnlyAttribute)));
        Assert.Equal([AccountRoles.Librarian, AccountRoles.LibraryManager], access.Roles);
    }

    private static ReaderAccount NewReader(string email, string code) => new()
    {
        FullName = "Test Reader",
        DateOfBirth = new DateOnly(2000, 1, 1),
        Email = email,
        PhoneNumber = "0900000000",
        StudentOrStaffCode = code,
        PasswordHash = "test"
    };

    private async Task<(ReaderAccount Reader, Book Book)> AddHistoryFixtureAsync()
    {
        var reader = NewReader("filtered@example.com", "R-FILTERED");
        var author = new Author { Name = "Filter author" };
        db.ReaderAccounts.Add(reader);
        db.Authors.Add(author);
        await db.SaveChangesAsync();
        var book = new Book { Title = "Filter book", AuthorId = author.Id };
        db.Books.Add(book);
        await db.SaveChangesAsync();
        return (reader, book);
    }

    private async Task AddLoanAsync(ReaderAccount reader, Book book, DateOnly loanDate)
    {
        db.BookLoans.Add(new BookLoan
        {
            ReaderAccountId = reader.Id,
            BookId = book.Id,
            LoanDate = loanDate,
            OriginalDueDate = loanDate.AddDays(14),
            DueDate = loanDate.AddDays(14)
        });
        await db.SaveChangesAsync();
    }

    private static async Task<ReaderLoanHistoryViewModel> GetModelAsync(
        ReaderLoanHistoryController controller, int readerId, DateOnly? startDate, DateOnly? endDate)
    {
        var result = Assert.IsType<ViewResult>(await controller.Details(readerId, startDate, endDate));
        return Assert.IsType<ReaderLoanHistoryViewModel>(result.Model);
    }
}
