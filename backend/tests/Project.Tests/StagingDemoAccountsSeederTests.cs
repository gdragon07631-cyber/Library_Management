using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class StagingDemoAccountsSeederTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;
    private readonly PasswordHasher<AdminAccount> staffHasher = new();
    private readonly PasswordHasher<ReaderAccount> readerHasher = new();

    public StagingDemoAccountsSeederTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
    }

    [Fact]
    public async Task SeedCreatesFourAuthenticAccountsWithExpectedRolesAndHashedPasswords()
    {
        await db.Database.EnsureCreatedAsync();
        var passwords = Passwords();

        var result = await Seeder().SeedAsync(passwords);

        Assert.Equal(4, result.Created.Count);
        Assert.All(result.Created.Values, created => Assert.True(created));
        var staff = await db.AdminAccounts.OrderBy(account => account.Role).ToListAsync();
        Assert.Equal(3, staff.Count);
        Assert.Equal(new[] { AccountRoles.Librarian, AccountRoles.LibraryManager, AccountRoles.SystemAdmin },
            staff.Select(account => account.Role).ToArray());
        Assert.All(staff, account => Assert.Equal(PasswordVerificationResult.Success,
            staffHasher.VerifyHashedPassword(account, account.PasswordHash, passwords[account.Role switch
            {
                AccountRoles.SystemAdmin => "SYSTEM_ADMIN",
                AccountRoles.LibraryManager => "LIBRARY_MANAGER",
                _ => "LIBRARIAN"
            }]!)));

        var reader = await db.ReaderAccounts.SingleAsync(account => account.Email == StagingDemoAccountsSeeder.ReaderEmail);
        Assert.Equal("Đang hoạt động", reader.Status);
        Assert.True(reader.EmailConfirmed);
        Assert.Equal(PasswordVerificationResult.Success, readerHasher.VerifyHashedPassword(reader, reader.PasswordHash, passwords["READER"]!));
        Assert.Equal(1, await db.LibraryCards.CountAsync(card => card.ReaderAccountId == reader.Id));
    }

    [Fact]
    public async Task RerunDoesNotDuplicateAccountsOrChangeExistingPasswords()
    {
        await db.Database.EnsureCreatedAsync();
        var passwords = Passwords();
        await Seeder().SeedAsync(passwords);
        var hashes = await db.AdminAccounts.ToDictionaryAsync(account => account.Email, account => account.PasswordHash);
        hashes.Add(StagingDemoAccountsSeeder.ReaderEmail,
            (await db.ReaderAccounts.SingleAsync(account => account.Email == StagingDemoAccountsSeeder.ReaderEmail)).PasswordHash);

        var result = await Seeder().SeedAsync(Passwords("different-passwords"));

        Assert.All(result.Created.Values, created => Assert.False(created));
        Assert.Equal(3, await db.AdminAccounts.CountAsync());
        Assert.Equal(1, await db.ReaderAccounts.CountAsync(account => account.Email == StagingDemoAccountsSeeder.ReaderEmail));
        Assert.Equal(1, await db.LibraryCards.CountAsync(card => card.CardCode == "STG-DEMO-CARD-LOGIN"));
        foreach (var account in await db.AdminAccounts.ToListAsync()) Assert.Equal(hashes[account.Email], account.PasswordHash);
        Assert.Equal(hashes[StagingDemoAccountsSeeder.ReaderEmail],
            (await db.ReaderAccounts.SingleAsync(account => account.Email == StagingDemoAccountsSeeder.ReaderEmail)).PasswordHash);
    }

    [Fact]
    public async Task MissingPasswordFailsBeforeCreatingAnyAccounts()
    {
        await db.Database.EnsureCreatedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Seeder().SeedAsync(new Dictionary<string, string?>()));

        Assert.Empty(await db.AdminAccounts.ToListAsync());
        Assert.Empty(await db.ReaderAccounts.ToListAsync());
    }

    private StagingDemoAccountsSeeder Seeder() => new(db, staffHasher, readerHasher);

    private static Dictionary<string, string?> Passwords(string prefix = "demo-password") => new()
    {
        ["SYSTEM_ADMIN"] = $"{prefix}-system-admin-1",
        ["LIBRARY_MANAGER"] = $"{prefix}-library-manager-2",
        ["LIBRARIAN"] = $"{prefix}-librarian-3",
        ["READER"] = $"{prefix}-reader-4"
    };

    public async ValueTask DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }
}
