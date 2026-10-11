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
}
