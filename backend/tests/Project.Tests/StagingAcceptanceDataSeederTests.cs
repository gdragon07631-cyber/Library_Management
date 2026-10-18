using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class StagingAcceptanceDataSeederTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;
    private readonly PasswordHasher<AdminAccount> staffHasher = new();
    private readonly PasswordHasher<ReaderAccount> readerHasher = new();

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

        var result = await CreateSeeder().SeedAsync(AccountPasswords());

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
        Assert.Equal(3, await db.AdminAccounts.CountAsync(account => account.Email.StartsWith("stg-demo-")));
        Assert.Equal(1, await db.ReaderAccounts.CountAsync(account => account.Email == StagingDemoAccountsSeeder.ReaderEmail));
    }

    [Fact]
    public async Task SeedCanBeRunAgainWithoutDuplicatingStagingRecords()
    {
        await db.Database.EnsureCreatedAsync();
        var seeder = CreateSeeder();

        await seeder.SeedAsync(AccountPasswords());
        var adminHashes = await db.AdminAccounts.ToDictionaryAsync(account => account.Email, account => account.PasswordHash);
        var demoReaderHash = (await db.ReaderAccounts.SingleAsync(account => account.Email == StagingDemoAccountsSeeder.ReaderEmail)).PasswordHash;
        var incidentLogCount = await db.BookIncidentAuditLogs.CountAsync();
        var copyHistoryCount = await db.BookCopyStatusHistories.CountAsync();
        var second = await seeder.SeedAsync(AccountPasswords());
        var third = await seeder.SeedAsync(AccountPasswords());

        Assert.Equal(200, second.Books);
        Assert.Equal(500, second.Copies);
        Assert.Equal(50, second.Readers);
        Assert.Equal(100, second.Loans);
        Assert.Equal((second.Books, second.Copies, second.Readers, second.Loans),
            (third.Books, third.Copies, third.Readers, third.Loans));
        Assert.Equal(20, await db.BookIncidents.CountAsync());
        Assert.Equal(incidentLogCount, await db.BookIncidentAuditLogs.CountAsync());
        Assert.Equal(copyHistoryCount, await db.BookCopyStatusHistories.CountAsync());
        Assert.Equal(3, await db.AdminAccounts.CountAsync(account => account.Email.StartsWith("stg-demo-")));
        Assert.Equal(1, await db.ReaderAccounts.CountAsync(account => account.Email == StagingDemoAccountsSeeder.ReaderEmail));
        foreach (var admin in await db.AdminAccounts.ToListAsync())
            Assert.Equal(adminHashes[admin.Email], admin.PasswordHash);
        Assert.Equal(demoReaderHash,
            (await db.ReaderAccounts.SingleAsync(account => account.Email == StagingDemoAccountsSeeder.ReaderEmail)).PasswordHash);
    }

    [Fact]
    public async Task ResetRestoresEditedSampleRowsAndRemovesRowsAddedToSampleScope()
    {
        await db.Database.EnsureCreatedAsync();
        var seeder = CreateSeeder();
        await seeder.SeedAsync(AccountPasswords());
        var firstBook = await db.Books.SingleAsync(book => book.Isbn == CreateSampleIsbn(1));
        firstBook.Title = "Tên bị sửa thủ công";
        var firstCopy = await db.BookCopies.FirstAsync(copy => copy.CopyCode == "DEMO-CPY-001-01");
        db.BookCopies.Add(new BookCopy
        {
            BookId = firstBook.Id,
            ShelfId = firstCopy.ShelfId,
            CopyCode = "STG-DEMO-CPY-MANUAL-01",
            ReceivedDate = new DateOnly(2026, 1, 1),
            CoverPrice = 10000m,
            Status = BookCopyStatus.Available,
            PhysicalCondition = BookCopyCondition.Good
        });
        db.ReaderAccounts.Add(new ReaderAccount
        {
            FullName = "Bạn đọc thêm thủ công",
            DateOfBirth = new DateOnly(2000, 1, 1),
            Email = "stg-demo-manual@example.test",
            PhoneNumber = "0900000999",
            StudentOrStaffCode = "STG-DEMO-RD-MANUAL",
            PasswordHash = "test-only-hash",
            Status = "Đang hoạt động",
            EmailConfirmed = true
        });
        await db.SaveChangesAsync();

        var result = await seeder.SeedAsync(AccountPasswords());

        Assert.Equal(200, result.Books);
        Assert.Equal(500, result.Copies);
        Assert.Equal(50, result.Readers);
        Assert.Equal(100, result.Loans);
        Assert.Equal("Dế Mèn phiêu lưu ký", (await db.Books.SingleAsync(book => book.Isbn == CreateSampleIsbn(1))).Title);
        Assert.False(await db.BookCopies.AnyAsync(copy => copy.CopyCode == "STG-DEMO-CPY-MANUAL-01"));
        Assert.False(await db.ReaderAccounts.AnyAsync(reader => reader.StudentOrStaffCode == "STG-DEMO-RD-MANUAL"));
        Assert.Equal(3, await db.AdminAccounts.CountAsync(account => account.Email.StartsWith("stg-demo-")));
        Assert.Equal(1, await db.ReaderAccounts.CountAsync(reader => reader.Email == StagingDemoAccountsSeeder.ReaderEmail));
    }

    [Fact]
    public async Task FailedReseedRollsBackDeletedDatasetAndKeepsAccountRows()
    {
        await db.Database.EnsureCreatedAsync();
        var seeder = CreateSeeder();
        await seeder.SeedAsync(AccountPasswords());
        var firstBook = await db.Books.SingleAsync(book => book.Isbn == CreateSampleIsbn(1));
        firstBook.Title = "Edit cần được giữ lại nếu reset lỗi";
        await db.SaveChangesAsync();
        await db.AdminAccounts.Where(account => account.Email == StagingDemoAccountsSeeder.SystemAdminEmail).ExecuteDeleteAsync();
        var invalidPasswords = AccountPasswords();
        invalidPasswords["SYSTEM_ADMIN"] = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.SeedAsync(invalidPasswords));

        db.ChangeTracker.Clear();
        Assert.Equal("Edit cần được giữ lại nếu reset lỗi", (await db.Books.SingleAsync(book => book.Isbn == CreateSampleIsbn(1))).Title);
        Assert.Equal(200, await db.Books.CountAsync(book => book.Isbn != null && book.Isbn.StartsWith("979000000")));
        Assert.Equal(500, await db.BookCopies.CountAsync(copy => copy.CopyCode.StartsWith("DEMO-CPY-") || copy.CopyCode.StartsWith("STG-DEMO-CPY-")));
        Assert.Equal(100, await db.BookLoans.CountAsync());
        Assert.Equal(2, await db.AdminAccounts.CountAsync(account => account.Email.StartsWith("stg-demo-")));
        Assert.Equal(1, await db.ReaderAccounts.CountAsync(account => account.Email == StagingDemoAccountsSeeder.ReaderEmail));
    }

    [Fact]
    public async Task ProductionEnvironmentGuardRejectsBeforeDatabaseCanBeChanged()
    {
        await db.Database.EnsureCreatedAsync();
        db.Authors.Add(new Author { Name = "Giữ nguyên trên production", Status = AuthorStatus.Active });
        await db.SaveChangesAsync();

        Assert.Throws<InvalidOperationException>(() =>
            StagingResetEnvironmentGuard.EnsureStaging(new TestHostEnvironment("Production")));
        StagingResetEnvironmentGuard.EnsureStaging(new TestHostEnvironment("Staging"));

        Assert.Equal(1, await db.Authors.CountAsync());
    }

    private StagingAcceptanceDataSeeder CreateSeeder() => new(db, staffHasher, readerHasher);

    private static Dictionary<string, string?> AccountPasswords() => new()
    {
        ["SYSTEM_ADMIN"] = "staging-reset-admin-2026",
        ["LIBRARY_MANAGER"] = "staging-reset-manager-2026",
        ["LIBRARIAN"] = "staging-reset-librarian-2026",
        ["READER"] = "staging-reset-reader-2026"
    };

    private static string CreateSampleIsbn(int sequence)
    {
        var body = $"979000000{sequence:000}";
        var sum = body.Select((digit, index) => (digit - '0') * (index % 2 == 0 ? 1 : 3)).Sum();
        return $"{body}{(10 - sum % 10) % 10}";
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Project.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }
}
