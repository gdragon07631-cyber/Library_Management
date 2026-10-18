using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed record LibraryDemoSeedResult(
    int AuthorsAdded,
    int CategoriesAdded,
    int WarehousesAdded,
    int ShelvesAdded,
    int BooksAdded,
    int CopiesAdded);

/// <summary>Adds repeatable sample catalog and inventory data without changing existing rows.</summary>
public sealed class LibraryDemoDataSeeder(ApplicationDbContext db)
{
    private sealed record BookSeed(string Title, int AuthorIndex, int CategoryIndex, string Publisher, int Year, int Pages, string Description);

    private static readonly string[] AuthorNames =
    [
        "Nguyễn Nhật Ánh", "Nam Cao", "Tô Hoài", "Vũ Trọng Phụng", "Nguyễn Du",
        "J. K. Rowling", "George Orwell", "Antoine de Saint-Exupéry", "Dale Carnegie", "Robert C. Martin"
    ];

    private static readonly string[] CategoryNames =
    [
        "Văn học Việt Nam", "Văn học nước ngoài", "Thiếu nhi", "Khoa học và công nghệ",
        "Lập trình và công nghệ thông tin", "Kỹ năng sống", "Lịch sử", "Kinh tế và quản trị",
        "Giáo dục", "Tâm lý học"
    ];

    private static readonly (string Code, string Name, string Description)[] WarehouseSeeds =
    [
        ("DEMO-KHO-01", "Kho sách tổng hợp mẫu", "Kho demo do lệnh seed tạo."),
        ("DEMO-KHO-02", "Kho sách tham khảo mẫu", "Kho demo do lệnh seed tạo."),
        ("DEMO-KHO-03", "Kho sách thiếu nhi mẫu", "Kho demo do lệnh seed tạo.")
    ];

    private static readonly BookSeed[] BookSeeds =
    [
        new("Dế Mèn phiêu lưu ký", 2, 2, "NXB Kim Đồng", 1941, 190, "Dữ liệu minh họa cho danh mục thư viện."),
        new("Tôi thấy hoa vàng trên cỏ xanh", 0, 0, "NXB Trẻ", 2010, 378, "Dữ liệu minh họa cho danh mục thư viện."),
        new("Cho tôi xin một vé đi tuổi thơ", 0, 0, "NXB Trẻ", 2008, 208, "Dữ liệu minh họa cho danh mục thư viện."),
        new("Truyện Kiều", 4, 0, "NXB Văn học", 1820, 320, "Dữ liệu minh họa cho danh mục thư viện."),
        new("Chí Phèo", 1, 0, "NXB Văn học", 1941, 160, "Dữ liệu minh họa cho danh mục thư viện."),
        new("Số đỏ", 3, 0, "NXB Văn học", 1936, 280, "Dữ liệu minh họa cho danh mục thư viện."),
        new("Harry Potter và hòn đá phù thủy", 5, 1, "NXB Trẻ", 1997, 366, "Dữ liệu minh họa cho danh mục thư viện."),
        new("Hoàng tử bé", 7, 1, "NXB Hội Nhà Văn", 1943, 96, "Dữ liệu minh họa cho danh mục thư viện."),
        new("1984", 6, 1, "NXB Hội Nhà Văn", 1949, 328, "Dữ liệu minh họa cho danh mục thư viện."),
        new("Đắc nhân tâm", 8, 5, "NXB Tổng hợp TP. Hồ Chí Minh", 1936, 320, "Dữ liệu minh họa cho danh mục thư viện."),
        new("Clean Code", 9, 4, "Prentice Hall", 2008, 464, "Dữ liệu minh họa; ISBN mẫu không đại diện ấn bản thật."),
        new("Cấu trúc dữ liệu và giải thuật", 9, 3, "NXB Đại học Quốc gia", 2022, 420, "Dữ liệu minh họa cho danh mục thư viện."),
        new("Lập trình C# hiện đại", 9, 4, "NXB Thông tin và Truyền thông", 2023, 380, "Dữ liệu minh họa cho danh mục thư viện."),
        new("Nhập môn trí tuệ nhân tạo", 9, 3, "NXB Khoa học và Kỹ thuật", 2022, 350, "Dữ liệu minh họa cho danh mục thư viện."),
        new("Lược sử thời gian", 9, 3, "NXB Trẻ", 1988, 256, "Dữ liệu minh họa cho danh mục thư viện."),
        new("Những tấm lòng cao cả", 9, 2, "NXB Văn học", 1886, 240, "Dữ liệu minh họa cho danh mục thư viện."),
        new("Tuổi thơ dữ dội", 0, 2, "NXB Kim Đồng", 1988, 814, "Dữ liệu minh họa cho danh mục thư viện."),
        new("Đất rừng phương Nam", 2, 2, "NXB Kim Đồng", 1957, 300, "Dữ liệu minh họa cho danh mục thư viện."),
        new("Nhật ký Đặng Thùy Trâm", 4, 6, "NXB Hội Nhà Văn", 2005, 400, "Dữ liệu minh họa cho danh mục thư viện."),
        new("Lịch sử Việt Nam giản lược", 4, 6, "NXB Chính trị Quốc gia", 2021, 360, "Dữ liệu minh họa cho danh mục thư viện."),
        new("Người giàu có nhất thành Babylon", 8, 7, "NXB Tổng hợp TP. Hồ Chí Minh", 1926, 208, "Dữ liệu minh họa cho danh mục thư viện."),
        new("Tư duy nhanh và chậm", 8, 9, "NXB Thế giới", 2011, 612, "Dữ liệu minh họa cho danh mục thư viện."),
        new("Tâm lý học đám đông", 8, 9, "NXB Tri thức", 1895, 240, "Dữ liệu minh họa cho danh mục thư viện."),
        new("Dạy con làm giàu", 8, 7, "NXB Trẻ", 1997, 336, "Dữ liệu minh họa cho danh mục thư viện."),
        new("Phương pháp học tập hiệu quả", 8, 8, "NXB Đại học Sư phạm", 2020, 220, "Dữ liệu minh họa cho danh mục thư viện.")
    ];

    public async Task<LibraryDemoSeedResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        var authorsAdded = 0;
        var categoriesAdded = 0;
        var warehousesAdded = 0;
        var shelvesAdded = 0;
        var booksAdded = 0;
        var copiesAdded = 0;

        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        foreach (var name in AuthorNames)
        {
            if (await db.Authors.AnyAsync(x => x.Name == name, cancellationToken)) continue;
            db.Authors.Add(new Author { Name = name, Status = AuthorStatus.Active, Note = "Dữ liệu demo." });
            authorsAdded++;
        }
        foreach (var name in CategoryNames)
        {
            if (await db.Categories.AnyAsync(x => x.Name == name, cancellationToken)) continue;
            db.Categories.Add(new Category { Name = name, Status = CategoryStatus.Active });
            categoriesAdded++;
        }
        foreach (var seed in WarehouseSeeds)
        {
            if (await db.Warehouses.AnyAsync(x => x.Code == seed.Code, cancellationToken)) continue;
            db.Warehouses.Add(new Warehouse
            {
                Code = seed.Code, Name = seed.Name, Description = seed.Description, Status = WarehouseStatus.Active
            });
            warehousesAdded++;
        }
        await db.SaveChangesAsync(cancellationToken);

        var authors = await db.Authors.AsNoTracking().ToDictionaryAsync(x => x.Name, cancellationToken);
        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(x => x.Name, cancellationToken);
        var warehouses = await db.Warehouses.Where(x => WarehouseSeeds.Select(s => s.Code).Contains(x.Code))
            .ToDictionaryAsync(x => x.Code, cancellationToken);

        for (var warehouseIndex = 0; warehouseIndex < WarehouseSeeds.Length; warehouseIndex++)
        {
            var warehouse = warehouses[WarehouseSeeds[warehouseIndex].Code];
            for (var shelfIndex = 1; shelfIndex <= 4; shelfIndex++)
            {
                var code = $"DEMO-K{warehouseIndex + 1:00}-K{shelfIndex:00}";
                if (await db.Shelves.AnyAsync(x => x.WarehouseId == warehouse.Id && x.Code == code, cancellationToken)) continue;
                db.Shelves.Add(new Shelf
                {
                    WarehouseId = warehouse.Id, Code = code, Name = $"Kệ {shelfIndex:00} - {warehouse.Name}",
                    Description = "Kệ demo do lệnh seed tạo.", Status = ShelfStatus.Active
                });
                shelvesAdded++;
            }
        }
        await db.SaveChangesAsync(cancellationToken);

        var shelves = await db.Shelves.AsNoTracking()
            .Where(x => x.Code.StartsWith("DEMO-K") && WarehouseSeeds.Select(s => s.Code).Contains(x.Warehouse!.Code))
            .OrderBy(x => x.Code).ToListAsync(cancellationToken);
        for (var index = 0; index < BookSeeds.Length; index++)
        {
            var seed = BookSeeds[index];
            var isbn = CreateSampleIsbn(index + 1);
            var book = await db.Books.Include(x => x.BookAuthors)
                .FirstOrDefaultAsync(x => x.Isbn == isbn, cancellationToken);
            if (book == null)
            {
                book = new Book
                {
                    Title = seed.Title, Isbn = isbn, AuthorId = authors[AuthorNames[seed.AuthorIndex]].Id,
                    CategoryId = categories[CategoryNames[seed.CategoryIndex]].Id, Publisher = seed.Publisher,
                    PublicationYear = seed.Year, PageCount = seed.Pages,
                    Description = seed.Description + " ISBN chỉ dùng để minh họa dữ liệu demo. " + StagingDemoMarkers.BookDescription
                };
                book.BookAuthors.Add(new BookAuthor { AuthorId = book.AuthorId, SortOrder = 0 });
                db.Books.Add(book);
                await db.SaveChangesAsync(cancellationToken);
                booksAdded++;
            }

            for (var copyIndex = 0; copyIndex < 2; copyIndex++)
            {
                var barcode = $"DEMO-CPY-{index + 1:000}-{copyIndex + 1:00}";
                if (await db.BookCopies.AnyAsync(x => x.CopyCode == barcode, cancellationToken)) continue;
                db.BookCopies.Add(new BookCopy
                {
                    BookId = book.Id, ShelfId = shelves[(index + copyIndex) % shelves.Count].Id,
                    CopyCode = barcode, ReceivedDate = new DateOnly(2026, 1, 15).AddDays(index),
                    CoverPrice = 85000m + index * 1500m, Status = BookCopyStatus.Available,
                    PhysicalCondition = BookCopyCondition.Good, Note = "Bản sao dữ liệu demo."
                });
                copiesAdded++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return new(authorsAdded, categoriesAdded, warehousesAdded, shelvesAdded, booksAdded, copiesAdded);
    }

    private static string CreateSampleIsbn(int sequence)
    {
        // ISBN-13-shaped illustrative values in the 979-0 range; they are explicitly demo identifiers.
        var body = $"979000000{sequence:000}";
        var sum = body.Select((digit, index) => (digit - '0') * (index % 2 == 0 ? 1 : 3)).Sum();
        var checkDigit = (10 - sum % 10) % 10;
        return $"{body}{checkDigit}";
    }
}
