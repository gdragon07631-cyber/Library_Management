using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Project.Controllers;
using Project.Data;
using Project.Filters;
using Project.Models;

namespace Project.Tests;

public sealed class ReaderLoanHistoryCsvTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;

    public ReaderLoanHistoryCsvTests()
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
    public async Task ExportContainsOnlyReadersLoansNewestFirstAndEscapesCsvFields()
    {
        var (reader, book) = await AddReaderAndBookAsync("csv@example.com", "R-CSV", "Book, \"Title\"\r\ncontinued");
        var other = await AddReaderAsync("other@example.com", "R-OTHER");
        var older = await AddLoanAsync(reader, book, new DateOnly(2026, 4, 1));
        var newer = await AddLoanAsync(reader, book, new DateOnly(2026, 4, 2));
        var (otherBook, _) = await AddBookAsync("Other book");
        await AddLoanAsync(other, otherBook, new DateOnly(2026, 4, 3));

        var file = Assert.IsType<FileContentResult>(await ExportAsync(reader.Id));
        var csv = ReadCsv(file);

        Assert.StartsWith("\"Mã phiếu\",\"Mã bạn đọc\"", csv);
        Assert.Matches(@"^lich-su-muon-tra_R-CSV_\d{8}\.csv$", file.FileDownloadName);
        Assert.Contains($"\"{newer.Id}\"", csv);
        Assert.Contains($"\"{older.Id}\"", csv);
        Assert.True(csv.IndexOf($"\"{newer.Id}\"", StringComparison.Ordinal) < csv.IndexOf($"\"{older.Id}\"", StringComparison.Ordinal));
        Assert.Contains("\"Book, \"\"Title\"\"\r\ncontinued\"", csv);
        Assert.Contains("\"2026-04-02\"", csv);
        Assert.DoesNotContain("R-OTHER", csv);
    }

    [Fact]
    public async Task ExportUsesInclusiveDateFiltersAndEmptyRangesOnlyContainHeader()
    {
        var (reader, book) = await AddReaderAndBookAsync("filter@example.com", "R-FILTER");
        var dates = new[]
        {
            new DateOnly(2026, 5, 9), new DateOnly(2026, 5, 10),
            new DateOnly(2026, 5, 12), new DateOnly(2026, 5, 13)
        };
        var loans = new List<BookLoan>();
        foreach (var date in dates) loans.Add(await AddLoanAsync(reader, book, date));

        var fromOnly = ReadCsv(Assert.IsType<FileContentResult>(await ExportAsync(reader.Id, dates[1], null)));
        Assert.DoesNotContain($"\"{loans[0].Id}\"", fromOnly);
        Assert.Contains($"\"{loans[1].Id}\"", fromOnly);
        Assert.Contains($"\"{loans[3].Id}\"", fromOnly);

        var throughOnly = ReadCsv(Assert.IsType<FileContentResult>(await ExportAsync(reader.Id, null, dates[2])));
        Assert.Contains($"\"{loans[0].Id}\"", throughOnly);
        Assert.Contains($"\"{loans[2].Id}\"", throughOnly);
        Assert.DoesNotContain($"\"{loans[3].Id}\"", throughOnly);

        var range = ReadCsv(Assert.IsType<FileContentResult>(await ExportAsync(reader.Id, dates[1], dates[2])));
        Assert.DoesNotContain($"\"{loans[0].Id}\"", range);
        Assert.Contains($"\"{loans[1].Id}\"", range);
        Assert.Contains($"\"{loans[2].Id}\"", range);
        Assert.DoesNotContain($"\"{loans[3].Id}\"", range);

        var empty = ReadCsv(Assert.IsType<FileContentResult>(await ExportAsync(reader.Id, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30))));
        Assert.Equal(1, empty.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("R-FILTER", empty);

        var neverBorrowed = await AddReaderAsync("never@example.com", "R-NEVER");
        var noHistory = Assert.IsType<FileContentResult>(await ExportAsync(neverBorrowed.Id));
        Assert.Equal("\uFEFF", Encoding.UTF8.GetString(noHistory.FileContents)[..1]);
        Assert.Equal(1, ReadCsv(noHistory).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("R-FILTER", ReadCsv(noHistory));
    }

    [Fact]
    public async Task ExportRejectsInvalidDatesAndUnknownReader()
    {
        var (reader, _) = await AddReaderAndBookAsync("invalid@example.com", "R-INVALID");
        var invalidRange = await ExportAsync(reader.Id, new DateOnly(2026, 6, 30), new DateOnly(2026, 6, 1));
        Assert.IsType<BadRequestObjectResult>(invalidRange);

        var malformedController = new ReaderLoanHistoryController(db);
        malformedController.ModelState.AddModelError("startDate", "Invalid date");
        Assert.IsType<BadRequestObjectResult>(await malformedController.ExportCsv(reader.Id));
        Assert.IsType<NotFoundObjectResult>(await ExportAsync(999));
    }

    [Fact]
    public void CsvExportControllerRemainsRestrictedToLibrarianAndManager()
    {
        var access = Assert.IsType<StaffOnlyAttribute>(Attribute.GetCustomAttribute(
            typeof(ReaderLoanHistoryController), typeof(StaffOnlyAttribute)));
        Assert.Equal([AccountRoles.Librarian, AccountRoles.LibraryManager], access.Roles);
    }

    private async Task<IActionResult> ExportAsync(int readerId, DateOnly? startDate = null, DateOnly? endDate = null) =>
        await new ReaderLoanHistoryController(db).ExportCsv(readerId, startDate, endDate);

    private static string ReadCsv(FileContentResult file) =>
        Encoding.UTF8.GetString(file.FileContents).TrimStart('\uFEFF');

    private async Task<(ReaderAccount Reader, Book Book)> AddReaderAndBookAsync(string email, string code, string title = "Test book")
    {
        var reader = await AddReaderAsync(email, code);
        var (book, _) = await AddBookAsync(title);
        return (reader, book);
    }

    private async Task<ReaderAccount> AddReaderAsync(string email, string code)
    {
        var reader = new ReaderAccount
        {
            FullName = "CSV Reader",
            DateOfBirth = new DateOnly(2000, 1, 1),
            Email = email,
            PhoneNumber = "0900000000",
            StudentOrStaffCode = code,
            PasswordHash = "test"
        };
        db.ReaderAccounts.Add(reader);
        await db.SaveChangesAsync();
        return reader;
    }

    private async Task<(Book Book, Author Author)> AddBookAsync(string title)
    {
        var author = new Author { Name = $"Author {Guid.NewGuid():N}" };
        db.Authors.Add(author);
        await db.SaveChangesAsync();
        var book = new Book { Title = title, AuthorId = author.Id };
        db.Books.Add(book);
        await db.SaveChangesAsync();
        return (book, author);
    }

    private async Task<BookLoan> AddLoanAsync(ReaderAccount reader, Book book, DateOnly loanDate)
    {
        var loan = new BookLoan
        {
            ReaderAccountId = reader.Id,
            BookId = book.Id,
            LoanDate = loanDate,
            OriginalDueDate = loanDate.AddDays(14),
            DueDate = loanDate.AddDays(14)
        };
        db.BookLoans.Add(loan);
        await db.SaveChangesAsync();
        return loan;
    }
}
