using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class TopHeldBookReportTests
{
    [Fact]
    public async Task CountsAllExistingHoldsOnceAndGroupsByBookId()
    {
        await using var db = CreateDb();
        db.Books.AddRange(Book(1, "Alpha"), Book(2, "Beta"), Book(3, "No holds"));
        db.BookHolds.AddRange(Hold(1, 1), Hold(2, 1), Hold(3, 1), Hold(1, 2), Hold(4, 2));
        await db.SaveChangesAsync();

        var result = await new LoanReportService(db).GetTopHeldBooksAsync();

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "Alpha", "Beta" }, result.Select(row => row.BookTitle));
        Assert.Equal(new[] { 3, 2 }, result.Select(row => row.PendingHoldCount));
        Assert.Equal(new[] { 1, 2 }, result.Select(row => row.Rank));
    }

    [Fact]
    public async Task TiesUseTitleThenBookIdAndResultsAreLimitedToTen()
    {
        await using var db = CreateDb();
        for (var id = 1; id <= 11; id++)
        {
            db.Books.Add(Book(id, $"Book {id:00}"));
            db.BookHolds.Add(Hold(id, id));
        }
        await db.SaveChangesAsync();

        var result = await new LoanReportService(db).GetTopHeldBooksAsync();

        Assert.Equal(10, result.Count);
        Assert.Equal(Enumerable.Range(1, 10), result.Select(row => row.Rank));
        Assert.Equal(Enumerable.Range(1, 10).Select(id => $"Book {id:00}"), result.Select(row => row.BookTitle));
    }

    [Fact]
    public async Task ReturnsEmptyListWhenNoHoldsExist()
    {
        await using var db = CreateDb();
        db.Books.Add(Book(1, "No holds"));
        await db.SaveChangesAsync();

        Assert.Empty(await new LoanReportService(db).GetTopHeldBooksAsync());
    }

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new ApplicationDbContext(options);
    }

    private static Book Book(int id, string title) => new() { Id = id, Title = title, AuthorId = 1 };
    private static BookHold Hold(int readerId, int bookId) => new()
    {
        ReaderAccountId = readerId, BookId = bookId, HeldAtUtc = DateTime.UtcNow
    };
}
