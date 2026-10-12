using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Services;

namespace Project.Tests;

public sealed class LibraryDemoDataSeederTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;

    public LibraryDemoDataSeederTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
    }

    [Fact]
    public async Task SeedIsIdempotentAndCreatesConsistentCatalogAndInventory()
    {
        await db.Database.EnsureCreatedAsync();
        var seeder = new LibraryDemoDataSeeder(db);

        var first = await seeder.SeedAsync();
        var second = await seeder.SeedAsync();

        Assert.Equal(new(10, 10, 3, 12, 25, 50), first);
        Assert.Equal(new(0, 0, 0, 0, 0, 0), second);
        Assert.Equal(10, await db.Authors.CountAsync());
        Assert.Equal(10, await db.Categories.CountAsync());
        Assert.Equal(3, await db.Warehouses.CountAsync());
        Assert.Equal(12, await db.Shelves.CountAsync());
        Assert.Equal(25, await db.Books.CountAsync());
        Assert.Equal(50, await db.BookCopies.CountAsync());
        var copies = await db.BookCopies.Include(x => x.Book).Include(x => x.Shelf).ThenInclude(shelf => shelf!.Warehouse).ToListAsync();
        Assert.All(copies, copy =>
        {
            Assert.NotNull(copy.Book);
            Assert.NotNull(copy.Shelf);
            Assert.NotNull(copy.Shelf!.Warehouse);
            Assert.StartsWith("DEMO-CPY-", copy.CopyCode);
        });
        Assert.All(await db.Books.Include(x => x.BookAuthors).ToListAsync(), book => Assert.NotEmpty(book.BookAuthors));
    }

    public async ValueTask DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }
}
