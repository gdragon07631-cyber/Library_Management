using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class LibraryReportTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private ApplicationDbContext db = null!;
    private LibraryReportService service = null!;
    private static readonly DateOnly Today = new(2026, 10, 17);
    private static readonly DateOnly Start = Today.AddDays(-29);

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        service = new LibraryReportService(db);
        await SeedAsync();
    }

    public async Task DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    [Fact]
    public async Task ReportQueriesReturnExpectedResultsFor500LoansAndLogTimings()
    {
        var summary = await service.GetSummaryAsync(Start, Today, Today);
        Assert.Equal(500, summary.TotalLoans);
        Assert.Equal(0, summary.TotalReturns);
        Assert.Equal(500, summary.OpenLoans);
        Assert.Equal(250, summary.OverdueLoans);
        Assert.Equal(50m, summary.OverdueRate);
        var books = await service.GetTopBorrowedBooksAsync(Start, Today);
        Assert.Equal(5, books.Count);
        Assert.Equal(100, books[0].LoanCount);
        Assert.Equal(1, books[0].PhysicalCopyCount);
        Assert.Equal(5, books[^1].PhysicalCopyCount);
        var holds = await service.GetTopHeldBooksAsync();
        Assert.Equal(5, holds.Count);
        Assert.Equal(20, holds[0].HoldCount);
        var daily = await service.GetDailyLoansAsync(Start, Today);
        Assert.Equal(30, daily.Count);
        Assert.Equal(500, daily.Sum(point => point.Count));

        var samples = new List<double>();
        for (var run = 0; run < 6; run++)
        {
            var watch = Stopwatch.StartNew();
            await service.GetSummaryAsync(Start, Today, Today);
            await service.GetTopBorrowedBooksAsync(Start, Today);
            await service.GetTopHeldBooksAsync();
            await service.GetDailyLoansAsync(Start, Today);
            watch.Stop();
            if (run > 0) samples.Add(watch.Elapsed.TotalMilliseconds);
            Console.WriteLine($"REPORT_SQLITE_500 run={run + 1} elapsed_ms={watch.Elapsed.TotalMilliseconds:F2}");
        }
        Console.WriteLine($"REPORT_SQLITE_500 measured_runs={samples.Count} average_ms={samples.Average():F2} max_ms={samples.Max():F2}");
    }

    private async Task SeedAsync()
    {
        var author = new Author { Name = "Performance fixture author" };
        var warehouse = new Warehouse { Code = "PERF", Name = "Performance fixture" };
        db.AddRange(author, warehouse); await db.SaveChangesAsync();
        var shelf = new Shelf { Code = "P-1", Name = "Performance shelf", WarehouseId = warehouse.Id };
        db.Shelves.Add(shelf);
        var books = Enumerable.Range(1, 5).Select(index => new Book { Title = $"Fixture book {index:00}", AuthorId = author.Id }).ToArray();
        db.AddRange(books); await db.SaveChangesAsync();
        foreach (var book in books)
            for (var copyIndex = 0; copyIndex < book.Id - books[0].Id + 1; copyIndex++)
                db.BookCopies.Add(new BookCopy { BookId = book.Id, ShelfId = shelf.Id, CopyCode = $"P-{book.Id}-{copyIndex}", Status = copyIndex % 2 == 0 ? "Sẵn sàng" : "Đang mượn" });
        var readers = Enumerable.Range(1, 20).Select(index => new ReaderAccount
        {
            FullName = $"Fixture Reader {index:00}", DateOfBirth = new DateOnly(2000, 1, 1), Email = $"fixture{index}@example.test",
            PhoneNumber = "0000000000", StudentOrStaffCode = $"FIX-{index:000}", PasswordHash = "fixture", Status = "Đang hoạt động"
        }).ToArray();
        db.ReaderAccounts.AddRange(readers); await db.SaveChangesAsync();
        foreach (var reader in readers)
            foreach (var book in books)
                db.BookHolds.Add(new BookHold { ReaderAccountId = reader.Id, BookId = book.Id, HeldAtUtc = DateTime.UtcNow });
        db.BookLoans.AddRange(Enumerable.Range(0, 500).Select(index => new BookLoan
        {
            BookId = books[index % books.Length].Id, ReaderAccountId = readers[index % readers.Length].Id,
            LoanDate = Start.AddDays(index % 30), OriginalDueDate = index % 2 == 0 ? Today.AddDays(-1) : Today.AddDays(1),
            DueDate = index % 2 == 0 ? Today.AddDays(-1) : Today.AddDays(1), CreatedAtUtc = DateTime.UtcNow.AddMinutes(-index)
        }));
        await db.SaveChangesAsync();
    }
}
