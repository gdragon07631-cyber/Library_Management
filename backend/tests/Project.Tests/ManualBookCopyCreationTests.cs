using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Project.Controllers;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class ManualBookCopyCreationTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;
    private readonly BookCopyService service;
    private readonly Book book;
    private readonly Book otherBook;
    private readonly Warehouse warehouse;
    private readonly Shelf shelf;

    public ManualBookCopyCreationTests()
    {
        connection.Open();
        db = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        var author = new Author { Name = "Tác giả" };
        warehouse = new() { Code = "K1", Name = "Kho 1" };
        db.AddRange(author, warehouse);
        db.SaveChanges();
        shelf = new() { Code = "S1", Name = "Kệ 1", WarehouseId = warehouse.Id };
        book = new() { Title = "Đầu sách A", AuthorId = author.Id };
        otherBook = new() { Title = "Đầu sách B", AuthorId = author.Id };
        db.AddRange(shelf, book, otherBook);
        db.SaveChanges();
        service = new(db);
    }

    private ManualBookCopyViewModel Valid(string code = "MANUAL-001") => new()
    {
        CopyCode = code, WarehouseId = warehouse.Id, ShelfId = shelf.Id,
        ReceivedDate = new(2026, 10, 5), CoverPrice = 125000.50m,
        PhysicalCondition = BookCopyCondition.Good
    };

    [Fact]
    public async Task ValidCopySavesAllFieldsWithAvailableStatusAndCorrectBook()
    {
        var model = Valid("  NEW-123  ");
        model.BookId = otherBook.Id; // Display-only input cannot override the route's book.
        var result = await service.AddManualAsync(book.Id, model);
        Assert.True(result.IsSuccess);
        var saved = await db.BookCopies.AsNoTracking().SingleAsync();
        Assert.Equal("NEW-123", saved.CopyCode);
        Assert.Equal(book.Id, saved.BookId);
        Assert.Equal(BookCopyStatus.Available, saved.Status);
        Assert.Equal(model.ShelfId, saved.ShelfId);
        Assert.Equal(model.ReceivedDate, saved.ReceivedDate);
        Assert.Equal(model.CoverPrice, saved.CoverPrice);
        Assert.Equal(model.PhysicalCondition, saved.PhysicalCondition);
    }

    [Theory]
    [InlineData("barcode")]
    [InlineData("warehouse")]
    [InlineData("shelf")]
    [InlineData("date")]
    [InlineData("price")]
    [InlineData("condition")]
    public async Task MissingRequiredFieldIsRejectedByValidationAndService(string field)
    {
        var model = Valid();
        switch (field)
        {
            case "barcode": model.CopyCode = "   "; break;
            case "warehouse": model.WarehouseId = 0; break;
            case "shelf": model.ShelfId = 0; break;
            case "date": model.ReceivedDate = null; break;
            case "price": model.CoverPrice = null; break;
            case "condition": model.PhysicalCondition = ""; break;
        }
        Assert.False(Validator.TryValidateObject(model, new(model), [], true));
        Assert.False((await service.AddManualAsync(book.Id, model)).IsSuccess);
        Assert.Empty(await db.BookCopies.ToListAsync());
    }

    [Theory]
    [InlineData("barcode")]
    [InlineData("price")]
    [InlineData("precision")]
    [InlineData("condition")]
    [InlineData("shelf")]
    [InlineData("warehouse")]
    public async Task InvalidInputCreatesNoData(string field)
    {
        var model = Valid();
        switch (field)
        {
            case "barcode": model.CopyCode = new string('A', 51); break;
            case "price": model.CoverPrice = -1; break;
            case "precision": model.CoverPrice = 1.001m; break;
            case "condition": model.PhysicalCondition = "Không hợp lệ"; break;
            case "shelf": model.ShelfId = int.MaxValue; break;
            case "warehouse": model.WarehouseId = int.MaxValue; break;
        }
        Assert.False((await service.AddManualAsync(book.Id, model)).IsSuccess);
        Assert.Empty(await db.BookCopies.ToListAsync());
    }

    [Fact]
    public async Task NewBarcodeAcceptedAndDuplicateAcrossBooksIdentifiesOwner()
    {
        var first = await service.AddManualAsync(otherBook.Id, Valid());
        Assert.True(first.IsSuccess);
        Assert.True((await service.AddManualAsync(book.Id, Valid("DIFFERENT"))).IsSuccess);
        var duplicate = await service.AddManualAsync(book.Id, Valid(" MANUAL-001 "));
        Assert.Equal(BookCopyUpdateStatus.DuplicateCode, duplicate.Status);
        Assert.Contains($"#{first.Copy!.Id}", duplicate.ErrorMessage);
        Assert.Contains(otherBook.Title, duplicate.ErrorMessage);
        Assert.Equal(2, await db.BookCopies.CountAsync());
    }

    [Fact]
    public async Task UniqueIndexRejectsDuplicateEvenWhenBypassingService()
    {
        await service.AddManualAsync(book.Id, Valid());
        db.BookCopies.Add(new() { BookId = otherBook.Id, ShelfId = shelf.Id, CopyCode = "MANUAL-001" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task CannotTransferCopyToAnotherBookThroughEditServiceOrController()
    {
        var copy = (await service.AddManualAsync(book.Id, Valid())).Copy!;
        var edit = new BookCopyEditViewModel
        {
            BookId = otherBook.Id, WarehouseId = warehouse.Id, ShelfId = shelf.Id,
            Status = BookCopyStatus.Available, PhysicalCondition = BookCopyCondition.Good
        };
        Assert.True((await service.UpdateAsync(copy.Id, edit, "staff")).IsSuccess);
        Assert.Equal(book.Id, (await db.BookCopies.AsNoTracking().SingleAsync()).BookId);
        await Controller().Edit(copy.Id, edit);
        Assert.Equal(book.Id, (await db.BookCopies.AsNoTracking().SingleAsync()).BookId);
    }

    [Fact]
    public async Task ControllerRedirectsToBookDetailsWithSuccessMessage()
    {
        var controller = Controller();
        var redirect = Assert.IsType<RedirectToActionResult>(await controller.Add(book.Id, Valid()));
        Assert.Equal("Book", redirect.ControllerName);
        Assert.Equal("Details", redirect.ActionName);
        Assert.Equal(book.Id, redirect.RouteValues!["id"]);
        Assert.Contains("thành công", controller.TempData["SuccessMessage"]!.ToString());
    }

    [Fact]
    public async Task ControllerRetainsValuesAndListsOnValidationFailure()
    {
        var controller = Controller();
        controller.ModelState.AddModelError("ReceivedDate", "Ngày nhập không hợp lệ.");
        var model = Valid();
        var view = Assert.IsType<ViewResult>(await controller.Add(book.Id, model));
        Assert.Same(model, view.Model);
        Assert.Single(model.Warehouses);
        Assert.Single(model.Shelves);
        Assert.Empty(await db.BookCopies.ToListAsync());
    }

    [Fact]
    public async Task ControllerShowsDuplicateAtBarcodeFieldWithoutSaving()
    {
        await service.AddManualAsync(otherBook.Id, Valid());
        var controller = Controller();
        Assert.IsType<ViewResult>(await controller.Add(book.Id, Valid()));
        Assert.Contains(otherBook.Title, controller.ModelState["CopyCode"]!.Errors.Single().ErrorMessage);
        Assert.Equal(1, await db.BookCopies.CountAsync());
    }

    [Fact]
    public async Task UnknownBookIsNotFound()
    {
        Assert.IsType<NotFoundObjectResult>(await Controller().Add(int.MaxValue));
        Assert.Equal(BookCopyUpdateStatus.NotFound, (await service.AddManualAsync(int.MaxValue, Valid())).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InactiveWarehouseOrShelfCannotReceiveCopy(bool disableWarehouse)
    {
        if (disableWarehouse) warehouse.Status = WarehouseStatus.Inactive;
        else shelf.Status = ShelfStatus.Inactive;
        await db.SaveChangesAsync();
        Assert.Equal(BookCopyUpdateStatus.InvalidShelf, (await service.AddManualAsync(book.Id, Valid())).Status);
        Assert.Empty(await db.BookCopies.ToListAsync());
    }

    [Fact]
    public async Task ShelfFromAnotherWarehouseIsRejected()
    {
        var otherWarehouse = new Warehouse { Code = "K2", Name = "Kho 2" };
        db.Add(otherWarehouse);
        await db.SaveChangesAsync();
        var model = Valid();
        model.WarehouseId = otherWarehouse.Id;
        Assert.Equal(BookCopyUpdateStatus.InvalidShelf, (await service.AddManualAsync(book.Id, model)).Status);
        Assert.Empty(await db.BookCopies.ToListAsync());
    }

    [Fact]
    public async Task AutomaticBarcodesAreSequentialUniqueAndAppearInDetails()
    {
        var bookService = new BookService(db, NullLogger<BookService>.Instance);
        for (var number = 1; number <= 8; number++)
        {
            var model = Valid("");
            model.GenerateBarcode = true;
            model.BookId = otherBook.Id;
            Assert.True(Validator.TryValidateObject(model, new(model), [], true));
            var result = await service.AddManualAsync(book.Id, model);
            Assert.True(result.IsSuccess);
            Assert.Equal($"LIB{number:D6}", result.Copy!.CopyCode);
            Assert.Matches("^LIB[0-9]{6}$", result.Copy.CopyCode);
            Assert.Equal(book.Id, result.Copy.BookId);
            Assert.Equal(BookCopyStatus.Available, result.Copy.Status);
            var details = (await bookService.GetBookDetailsAsync(book.Id))!;
            Assert.Equal(number, details.AvailableCopies);
            Assert.Equal(number, details.TotalCopies);
            Assert.Contains(details.Copies, copy => copy.CopyCode == result.Copy.CopyCode);
        }
        Assert.Equal(8, await db.BookCopies.Select(copy => copy.CopyCode).Distinct().CountAsync());
        Assert.Empty((await bookService.GetBookDetailsAsync(otherBook.Id))!.Copies);
    }

    [Fact]
    public async Task AutomaticSequenceUsesLargestManualCpyCodeAndIgnoresOtherFormats()
    {
        foreach (var code in new[] { "CPY000010", "CPY000004", "LIB999999", "999999", "ABC999999", "CPY99999", "CPY9999999", "CPYABCDEF" })
            Assert.True((await service.AddManualAsync(otherBook.Id, Valid(code))).IsSuccess);
        var model = Valid("IGNORED");
        model.GenerateBarcode = true;
        Assert.Equal("CPY000011", (await service.AddManualAsync(book.Id, model)).Copy!.CopyCode);
    }

    [Fact]
    public async Task SwitchingBetweenManualAndAutomaticKeepsManualValidationAndRejectsDuplicates()
    {
        var model = Valid("");
        Assert.False((await service.AddManualAsync(book.Id, model)).IsSuccess);
        model.GenerateBarcode = true;
        Assert.Equal("CPY000001", (await service.AddManualAsync(book.Id, model)).Copy!.CopyCode);
        model.GenerateBarcode = false;
        Assert.False((await service.AddManualAsync(book.Id, model)).IsSuccess);
        model.CopyCode = "HAND-123";
        Assert.True((await service.AddManualAsync(book.Id, model)).IsSuccess);
        var duplicate = await service.AddManualAsync(otherBook.Id, model);
        Assert.Equal(BookCopyUpdateStatus.DuplicateCode, duplicate.Status);
        Assert.Contains(book.Title, duplicate.ErrorMessage);
        model.GenerateBarcode = true;
        Assert.Equal("CPY000002", (await service.AddManualAsync(book.Id, model)).Copy!.CopyCode);
        model.GenerateBarcode = false;
        model.CopyCode = "CPY000002";
        Assert.Equal(BookCopyUpdateStatus.DuplicateCode, (await service.AddManualAsync(book.Id, model)).Status);
        Assert.Equal(3, await db.BookCopies.CountAsync());
    }

    [Fact]
    public async Task ExhaustedSequenceCreatesNoCopyAndManualEntryStillWorks()
    {
        await service.AddManualAsync(otherBook.Id, Valid("CPY999999"));
        var model = Valid("");
        model.GenerateBarcode = true;
        var result = await service.AddManualAsync(book.Id, model);
        Assert.False(result.IsSuccess);
        Assert.Contains("CPY999999", result.ErrorMessage);
        Assert.Equal(1, await db.BookCopies.CountAsync());
        Assert.True((await service.AddManualAsync(book.Id, Valid("MANUAL-AFTER-LIMIT"))).IsSuccess);
    }

    [Fact]
    public async Task AutomaticCreationReportsGeneratedBarcodeAndRedirectsToDetails()
    {
        var model = Valid("");
        model.GenerateBarcode = true;
        var controller = Controller();
        var result = Assert.IsType<RedirectToActionResult>(await controller.Add(book.Id, model));
        Assert.Equal("Details", result.ActionName);
        Assert.Equal(book.Id, result.RouteValues!["id"]);
        Assert.Contains("Mã vạch vừa sinh: CPY000001", controller.TempData["SuccessMessage"]!.ToString());
    }

    [Fact]
    public async Task AutomaticModeStillRequiresOtherFields()
    {
        var model = Valid("");
        model.GenerateBarcode = true;
        model.ReceivedDate = null;
        Assert.False((await service.AddManualAsync(book.Id, model)).IsSuccess);
        Assert.Empty(await db.BookCopies.ToListAsync());
    }

    [Fact]
    public async Task AutomaticCreationRetriesIfAnotherRequestSavesItsCandidateFirst()
    {
        var interceptor = new CompetingBarcodeInsert(async () =>
        {
            await using var winner = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection).Options);
            winner.BookCopies.Add(new() { BookId = otherBook.Id, ShelfId = shelf.Id, CopyCode = "CPY000001" });
            await winner.SaveChangesAsync();
        });
        await using var racingDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection).AddInterceptors(interceptor).Options);
        var model = Valid("");
        model.GenerateBarcode = true;
        var result = await new BookCopyService(racingDb).AddManualAsync(book.Id, model);
        Assert.True(result.IsSuccess);
        Assert.Equal("CPY000002", result.Copy!.CopyCode);
        Assert.Equal(book.Id, result.Copy.BookId);
        Assert.Equal(2, await db.BookCopies.CountAsync());
        Assert.Equal(2, await db.BookCopies.Select(copy => copy.CopyCode).Distinct().CountAsync());
    }

    private sealed class CompetingBarcodeInsert(Func<Task> insert) : SaveChangesInterceptor
    {
        private bool inserted;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!inserted)
            {
                inserted = true;
                await insert();
            }
            return result;
        }
    }

    private BookCopyController Controller()
    {
        var context = new DefaultHttpContext();
        return new(service, new AuditLogService(db, NullLogger<AuditLogService>.Instance))
        {
            ControllerContext = new() { HttpContext = context },
            TempData = new TempDataDictionary(context, new EmptyTempDataProvider())
        };
    }

    private sealed class EmptyTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    public void Dispose() { db.Dispose(); connection.Dispose(); }
}
