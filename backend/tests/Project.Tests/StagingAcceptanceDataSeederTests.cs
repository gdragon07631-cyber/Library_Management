using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class StagingAcceptanceDataSeederTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;

    public StagingAcceptanceDataSeederTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection).Options);
    }

    [Fact]
    public async Task SeedCreatesRequiredCatalogReadersLoansAndAllCurrentLoanStates()
    {
        await db.Database.EnsureCreatedAsync();

        var result = await new StagingAcceptanceDataSeeder(db).SeedAsync();

        Assert.Equal(200, result.Books);
        Assert.Equal(500, result.Copies);
        Assert.Equal(50, result.Readers);
        Assert.Equal(100, result.Loans);
        Assert.Equal(20, result.LoanStates["Đang mượn"]);
        Assert.Equal(20, result.LoanStates["Quá hạn chưa trả"]);
        Assert.Equal(20, result.LoanStates["Đã trả đúng hạn"]);
        Assert.Equal(20, result.LoanStates["Đã trả trễ hạn"]);
        Assert.Equal(10, result.LoanStates["Đóng do mất"]);
        Assert.Equal(10, result.LoanStates["Đóng do hỏng nặng"]);

        var demoBooks = await db.Books.Where(book => book.Isbn != null && book.Isbn.StartsWith("979000000"))
            .Include(book => book.BookAuthors).ToListAsync();
        Assert.Equal(200, demoBooks.Count);
        Assert.All(demoBooks, book =>
        {
            Assert.NotNull(book.AuthorId);
            Assert.NotNull(book.CategoryId);
            Assert.NotEmpty(book.BookAuthors);
            Assert.False(string.IsNullOrWhiteSpace(book.Title));
        });

        var demoCopies = await db.BookCopies.Where(copy =>
            copy.CopyCode.StartsWith("DEMO-CPY-") || copy.CopyCode.StartsWith("STG-DEMO-CPY-"))
            .Include(copy => copy.Book).Include(copy => copy.Shelf).ToListAsync();
        Assert.Equal(500, demoCopies.Count);
        Assert.Equal(500, demoCopies.Select(copy => copy.CopyCode).Distinct().Count());
        Assert.Equal(40, demoCopies.Count(copy => copy.Status == BookCopyStatus.OnLoan));
        Assert.Equal(20, demoCopies.Count(copy => copy.Status == BookCopyStatus.Removed));
        Assert.All(demoCopies, copy =>
        {
            Assert.NotNull(copy.Book);
            Assert.NotNull(copy.Shelf);
        });

        var readers = await db.ReaderAccounts.Where(reader => reader.StudentOrStaffCode.StartsWith("STG-DEMO-RD-")).ToListAsync();
        Assert.Equal(50, readers.Count);
        Assert.Equal(50, readers.Select(reader => reader.StudentOrStaffCode).Distinct().Count());
        Assert.All(readers, reader =>
        {
            Assert.Equal("Đang hoạt động", reader.Status);
            Assert.EndsWith("@example.test", reader.Email);
            Assert.False(string.IsNullOrWhiteSpace(reader.PasswordHash));
        });

        var loans = await db.BookLoans.Where(loan => loan.BookCopy != null &&
                (loan.BookCopy.CopyCode.StartsWith("DEMO-CPY-") || loan.BookCopy.CopyCode.StartsWith("STG-DEMO-CPY-")))
            .Include(loan => loan.BookCopy).Include(loan => loan.Book).Include(loan => loan.ReaderAccount)
            .ToListAsync();
        Assert.Equal(100, loans.Count);
        Assert.Equal(100, loans.Select(loan => loan.BookCopyId).Distinct().Count());
        Assert.All(loans, loan =>
        {
            Assert.NotNull(loan.BookCopy);
            Assert.NotNull(loan.Book);
            Assert.NotNull(loan.ReaderAccount);
            Assert.Equal(loan.BookId, loan.BookCopy!.BookId);
            Assert.True(loan.DueDate >= loan.LoanDate);
            if (loan.ReturnedAtUtc is { } returnedAt)
                Assert.True(LibraryCalendarDate.DateFromUtc(returnedAt) >= loan.LoanDate);
        });

        Assert.Equal(20, await db.BookIncidents.CountAsync(item =>
            item.IncidentType == BookIncidentType.Lost || item.IncidentType == BookIncidentType.Damaged));
        Assert.Equal(10, await db.BookIncidents.CountAsync(item => item.IncidentType == BookIncidentType.Lost));
        Assert.Equal(10, await db.BookIncidents.CountAsync(item => item.IncidentType == BookIncidentType.Damaged));
        Assert.Equal(20, await db.BookCopyStatusHistories.CountAsync(item => item.ToStatus == BookCopyStatus.Removed));
        Assert.Equal(40, await db.BookLoans.CountAsync(loan => loan.ReturnedAtUtc == null && loan.CloseReason == null));
        Assert.Equal(40, await db.BookLoans.CountAsync(loan => loan.CloseReason == BookLoanCloseReason.Returned));
    }

    [Fact]
    public async Task SeedCanBeRunAgainWithoutDuplicatingStagingRecords()
    {
        await db.Database.EnsureCreatedAsync();
        var seeder = new StagingAcceptanceDataSeeder(db);

        await seeder.SeedAsync();
        var second = await seeder.SeedAsync();

        Assert.Equal(200, second.Books);
        Assert.Equal(500, second.Copies);
        Assert.Equal(50, second.Readers);
        Assert.Equal(100, second.Loans);
        Assert.Equal(20, await db.BookIncidents.CountAsync());
    }

    public async ValueTask DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }
}
