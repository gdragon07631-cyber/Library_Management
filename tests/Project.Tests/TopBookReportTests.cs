using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class TopBookReportTests
{
    private static readonly DateOnly Start = new(2026, 9, 18);
    private static readonly DateOnly End = new(2026, 10, 17);

    [Fact]
    public async Task GroupsLoansByBookCountsAllPhysicalCopiesAndUsesInclusiveDateRange()
    {
        await using var db = CreateDb();
        db.Books.AddRange(Book(1, "Alpha"), Book(2, "Beta"), Book(3, "No loans"));
        db.BookLoans.AddRange(
            Loan(1, Start), Loan(1, End), Loan(1, End.AddDays(-1)),
            Loan(2, End), Loan(2, End), Loan(2, Start.AddDays(-1)));
        db.BookCopies.AddRange(
            Copy(1, 1, "A-1", "Sẵn sàng"), Copy(2, 1, "A-2", "Hỏng"), Copy(3, 1, "A-3", "Mất"),
            Copy(4, 3, "C-1", "Sẵn sàng"));
        await db.SaveChangesAsync();

        var result = await new DemoLoanReportService(db).GetTopBooksAsync(Start, End);

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "Alpha", "Beta" }, result.Select(row => row.BookTitle));
        Assert.Equal(new[] { 1, 2 }, result.Select(row => row.Rank));
        Assert.Equal(new[] { 3, 2 }, result.Select(row => row.LoanCount));
        Assert.Equal(new[] { 3, 0 }, result.Select(row => row.PhysicalCopyCount));
    }

    [Fact]
    public async Task ReturnsAtMostTenBooksWithStableTitleOrderingForTies()
    {
        await using var db = CreateDb();
        for (var id = 1; id <= 11; id++)
        {
            db.Books.Add(Book(id, $"Book {id:00}"));
            db.BookLoans.Add(Loan(id, Start));
        }
        await db.SaveChangesAsync();

        var result = await new DemoLoanReportService(db).GetTopBooksAsync(Start, End);

        Assert.Equal(10, result.Count);
        Assert.Equal(Enumerable.Range(1, 10), result.Select(row => row.Rank));
        Assert.Equal(Enumerable.Range(1, 10).Select(id => $"Book {id:00}"), result.Select(row => row.BookTitle));
    }

    [Fact]
    public async Task ReturnsEmptyListWhenThereAreNoLoanEventsInRange()
    {
        await using var db = CreateDb();
        db.Books.Add(Book(1, "Outside range"));
        db.BookLoans.Add(Loan(1, Start.AddDays(-1)));
        await db.SaveChangesAsync();

        var result = await new DemoLoanReportService(db).GetTopBooksAsync(Start, End);

        Assert.Empty(result);
    }

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new ApplicationDbContext(options);
    }

    private static Book Book(int id, string title) => new() { Id = id, Title = title, AuthorId = 1 };
    private static BookLoan Loan(int bookId, DateOnly date) => new()
    {
        BookId = bookId, ReaderAccountId = 1, LoanDate = date,
        OriginalDueDate = date.AddDays(14), DueDate = date.AddDays(14)
    };
    private static BookCopy Copy(long id, int bookId, string code, string status) => new()
    {
        Id = id, BookId = bookId, ShelfId = 1, CopyCode = code, Status = status
    };
}
