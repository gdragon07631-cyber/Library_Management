using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Data;
using System.Globalization;
using Project.Data;
using Project.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ZXing;
using ZXing.Common;

namespace Project.Services;

public sealed class BookCopyService(ApplicationDbContext dbContext, IOptions<BookCopyLabelPrintOptions>? labelOptions = null,
    IBookHoldFulfillmentService? holdFulfillmentService = null) : IBookCopyService
{
    private const int BarcodeLength = 6;
    private const string BarcodePrefix = "CPY";
    private const int MaximumBarcodeNumber = 999999;
    private readonly BookCopyLabelPrintOptions labelLayout = labelOptions?.Value ?? new();
    private readonly IBookHoldFulfillmentService holdFulfillmentService = holdFulfillmentService ??
        new BookHoldFulfillmentService(dbContext, new BookLoanService(dbContext, new WorkingScheduleService(dbContext)));

    public async Task<BookCopyIndexViewModel?> GetBookCopiesAsync(int bookId, CancellationToken cancellationToken = default)
    {
        var title = await dbContext.Books.AsNoTracking()
            .Where(book => book.Id == bookId).Select(book => book.Title)
            .SingleOrDefaultAsync(cancellationToken);
        if (title is null) return null;

        return new BookCopyIndexViewModel
        {
            BookId = bookId,
            BookTitle = title,
            Copies = await dbContext.BookCopies.AsNoTracking()
                .Include(copy => copy.Shelf).ThenInclude(shelf => shelf!.Warehouse)
                .Where(copy => copy.BookId == bookId)
                .OrderBy(copy => copy.CopyCode)
                .ToListAsync(cancellationToken),
            Warehouses = await GetActiveWarehousesAsync(cancellationToken),
            Shelves = await GetActiveShelvesAsync(cancellationToken)
        };
    }

    public async Task<BookCopyEditViewModel?> GetForEditAsync(long copyId, CancellationToken cancellationToken = default)
    {
        var copy = await dbContext.BookCopies.AsNoTracking()
            .Include(item => item.Book)
            .Include(item => item.Shelf)
            .SingleOrDefaultAsync(item => item.Id == copyId, cancellationToken);
        if (copy is null) return null;

        return new BookCopyEditViewModel
        {
            Id = copy.Id,
            CopyCode = copy.CopyCode,
            BookId = copy.BookId,
            BookTitle = copy.Book?.Title ?? string.Empty,
            CurrentStatus = copy.Status,
            WarehouseId = copy.Shelf?.WarehouseId ?? 0,
            ShelfId = copy.ShelfId,
            PhysicalCondition = copy.PhysicalCondition,
            Status = copy.Status,
            Reason = copy.StatusReason,
            Note = copy.Note
        };
    }

    public async Task<IReadOnlyList<Warehouse>> GetActiveWarehousesAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Warehouses.AsNoTracking()
            .Where(warehouse => warehouse.Status == WarehouseStatus.Active)
            .OrderBy(warehouse => warehouse.Code)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Shelf>> GetActiveShelvesAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Shelves.AsNoTracking()
            .Include(shelf => shelf.Warehouse)
            .Where(shelf => shelf.Status == ShelfStatus.Active && shelf.Warehouse!.Status == WarehouseStatus.Active)
            .OrderBy(shelf => shelf.Warehouse!.Code).ThenBy(shelf => shelf.Code)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<BookCopyStatusHistory>> GetHistoryAsync(long copyId, CancellationToken cancellationToken = default) =>
        await dbContext.BookCopyStatusHistories.AsNoTracking()
            .Where(history => history.BookCopyId == copyId)
            .OrderByDescending(history => history.ChangedAtUtc).ThenByDescending(history => history.Id)
            .ToListAsync(cancellationToken);

    public async Task<BookCopyUpdateResult> AddAsync(int bookId, NewBookCopyViewModel model, CancellationToken cancellationToken = default)
    {
        if (!await dbContext.Books.AnyAsync(book => book.Id == bookId, cancellationToken))
            return new(BookCopyUpdateStatus.NotFound, "Không tìm thấy đầu sách.");
        if (!BookCopyCondition.All.Contains(model.PhysicalCondition))
            return new(BookCopyUpdateStatus.InvalidCondition, "Tình trạng vật lý không hợp lệ.");
        if (!await IsActiveShelfAsync(model.ShelfId, warehouseId: null, cancellationToken))
            return new(BookCopyUpdateStatus.InvalidShelf, "Kệ không tồn tại hoặc đã ngừng sử dụng.");

        var code = model.CopyCode.Trim();
        if (await dbContext.BookCopies.AnyAsync(copy => copy.CopyCode == code, cancellationToken))
            return new(BookCopyUpdateStatus.DuplicateCode, $"Mã vạch {code} đã được dùng cho một bản sao khác.");
        if (await dbContext.LibraryCards.AnyAsync(card => card.CardCode == code, cancellationToken))
            return new(BookCopyUpdateStatus.DuplicateCode, $"Mã vạch {code} trùng với mã thẻ bạn đọc. Vui lòng nhập mã khác.");

        var copy = new BookCopy
        {
            BookId = bookId,
            ShelfId = model.ShelfId,
            CopyCode = code,
            Status = BookCopyStatus.Available,
            PhysicalCondition = model.PhysicalCondition,
            Note = string.IsNullOrWhiteSpace(model.Note) ? null : model.Note.Trim()
        };
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        dbContext.BookCopies.Add(copy);
        await dbContext.SaveChangesAsync(cancellationToken);
        await holdFulfillmentService.FulfillNextAsync(bookId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(BookCopyUpdateStatus.Success, Copy: copy);
    }

    public async Task<BookCopyBatchCreateResult> AddBatchAsync(int bookId, NewBookCopyBatchViewModel model, CancellationToken cancellationToken = default)
    {
        if (model.Quantity is < 1 or > 50)
            return new(BookCopyBatchCreateStatus.InvalidQuantity, "Số lượng phải từ 1 đến 50.");
        if (model.ReceivedDate is null)
            return new(BookCopyBatchCreateStatus.InvalidReceivedDate, "Vui lòng chọn ngày nhập.");
        if (!await dbContext.Books.AnyAsync(book => book.Id == bookId, cancellationToken))
            return new(BookCopyBatchCreateStatus.NotFound, "Không tìm thấy đầu sách.");
        if (!await IsActiveShelfAsync(model.ShelfId, model.WarehouseId, cancellationToken))
            return new(BookCopyBatchCreateStatus.InvalidShelf, "Kệ đã chọn không thuộc kho đã chọn hoặc đã ngừng sử dụng.");

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var lastNumber = await GetLastBarcodeNumberAsync(cancellationToken);
        var copies = new List<BookCopy>(model.Quantity);
        var skippedBarcodes = new List<string>();
        var nextNumber = lastNumber + 1;
        while (copies.Count < model.Quantity)
        {
            if (nextNumber > MaximumBarcodeNumber)
                return new(BookCopyBatchCreateStatus.BarcodeSequenceExhausted, "Đã hết dải mã vạch gồm 6 chữ số.");

            var code = FormatBarcode(nextNumber++);
            if (await dbContext.BookCopies.AsNoTracking().AnyAsync(copy => copy.CopyCode == code, cancellationToken) ||
                await dbContext.LibraryCards.AsNoTracking().AnyAsync(card => card.CardCode == code, cancellationToken))
            {
                skippedBarcodes.Add(code);
                continue;
            }

            copies.Add(new BookCopy
            {
                BookId = bookId,
                ShelfId = model.ShelfId,
                CopyCode = code,
                ReceivedDate = model.ReceivedDate.Value,
                Status = BookCopyStatus.Available,
                PhysicalCondition = BookCopyCondition.Good
            });
        }

        dbContext.BookCopies.AddRange(copies);
        await dbContext.SaveChangesAsync(cancellationToken);

        var createdIds = copies.Select(copy => copy.Id).ToArray();
        var savedCopies = await dbContext.BookCopies.AsNoTracking()
            .Include(copy => copy.Shelf).ThenInclude(shelf => shelf!.Warehouse)
            .Where(copy => createdIds.Contains(copy.Id))
            .OrderBy(copy => copy.CopyCode)
            .ToListAsync(cancellationToken);
        await holdFulfillmentService.FulfillNextAsync(bookId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        savedCopies = await dbContext.BookCopies.AsNoTracking()
            .Include(copy => copy.Shelf).ThenInclude(shelf => shelf!.Warehouse)
            .Where(copy => createdIds.Contains(copy.Id))
            .OrderBy(copy => copy.CopyCode)
            .ToListAsync(cancellationToken);
        return new(BookCopyBatchCreateStatus.Success, Copies: savedCopies, SkippedBarcodes: skippedBarcodes);
    }

    public async Task<BookCopyBarcodePreviewResult> PreviewBatchAsync(int quantity, CancellationToken cancellationToken = default)
    {
        if (quantity is < 1 or > 50)
            return new(false, ErrorMessage: "Số lượng phải là số nguyên từ 1 đến 50.");

        var startNumber = await GetLastBarcodeNumberAsync(cancellationToken) + 1;
        var endNumber = startNumber + quantity - 1;
        if (endNumber > MaximumBarcodeNumber)
            return new(false, ErrorMessage: "Đã hết dải mã vạch gồm 6 chữ số.");

        var startBarcode = FormatBarcode(startNumber);
        var endBarcode = FormatBarcode(endNumber);
        return new(true, startBarcode, endBarcode, $"{startBarcode} - {endBarcode}");
    }

    public async Task<BookCopyLabelsViewModel?> GetLabelsForCopiesAsync(int bookId, IReadOnlyList<long> copyIds, CancellationToken cancellationToken = default)
    {
        if (copyIds.Count is < 1 or > 50 || copyIds.Distinct().Count() != copyIds.Count)
            return null;

        var copies = await dbContext.BookCopies.AsNoTracking()
            .Include(copy => copy.Book)
            .Include(copy => copy.Shelf).ThenInclude(shelf => shelf!.Warehouse)
            .Where(copy => copy.BookId == bookId && copyIds.Contains(copy.Id))
            .ToListAsync(cancellationToken);
        if (copies.Count != copyIds.Count) return null;

        var copiesById = copies.ToDictionary(copy => copy.Id);
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.CODE_128,
            Options = new EncodingOptions
            {
                Width = labelLayout.BarcodeWidthPx,
                Height = labelLayout.BarcodeHeightPx,
                Margin = 8,
                PureBarcode = true
            }
        };
        var labels = new List<BookCopyLabelViewModel>(copyIds.Count);
        foreach (var copyId in copyIds)
        {
            var copy = copiesById[copyId];
            var pixels = writer.Write(copy.CopyCode);
            using var image = Image.LoadPixelData<Bgra32>(pixels.Pixels, pixels.Width, pixels.Height);
            await using var stream = new MemoryStream();
            await image.SaveAsPngAsync(stream, cancellationToken);
            labels.Add(new BookCopyLabelViewModel
            {
                CopyId = copy.Id,
                CopyCode = copy.CopyCode,
                BookTitle = copy.Book?.Title ?? string.Empty,
                WarehouseCode = copy.Shelf?.Warehouse?.Code ?? string.Empty,
                ShelfCode = copy.Shelf?.Code ?? string.Empty,
                ReceivedDate = copy.ReceivedDate,
                BarcodeImageDataUri = $"data:image/png;base64,{Convert.ToBase64String(stream.ToArray())}"
            });
        }

        return new BookCopyLabelsViewModel
        {
            BookId = bookId,
            BookTitle = copiesById[copyIds[0]].Book?.Title ?? string.Empty,
            Layout = labelLayout,
            Labels = labels
        };
    }

    public async Task<BookCopyUpdateResult> UpdateAsync(long copyId, BookCopyEditViewModel model, string changedBy, CancellationToken cancellationToken = default)
    {
        var copy = await dbContext.BookCopies.SingleOrDefaultAsync(item => item.Id == copyId, cancellationToken);
        if (copy is null) return new(BookCopyUpdateStatus.NotFound, "Không tìm thấy bản sao.");
        if (!BookCopyCondition.All.Contains(model.PhysicalCondition))
            return new(BookCopyUpdateStatus.InvalidCondition, "Tình trạng vật lý không hợp lệ.");
        if (!await IsActiveShelfAsync(model.ShelfId, model.WarehouseId, cancellationToken))
            return new(BookCopyUpdateStatus.InvalidShelf, "Kệ đã chọn không thuộc kho đã chọn hoặc đã ngừng sử dụng.");

        var statusChanged = !string.Equals(copy.Status, model.Status, StringComparison.Ordinal);
        if (statusChanged)
        {
            if (model.Status == BookCopyStatus.UnderRepair && copy.Status == BookCopyStatus.OnLoan)
                return new(BookCopyUpdateStatus.OnActiveLoan,
                    "Không thể chuyển bản sao sang Đang sửa chữa vì bản sao đang thuộc phiếu mượn chưa trả.");
            if (copy.Status == BookCopyStatus.OnHold)
                return new(BookCopyUpdateStatus.StatusManagedByHold,
                    "Không thể đổi trạng thái: bản sao đang được giữ cho một đơn đặt giữ.");
            if (!BookCopyStatus.Editable.Contains(model.Status))
                return new(BookCopyUpdateStatus.InvalidStatus, "Trạng thái chỉ được chọn Sẵn sàng hoặc Đang sửa chữa.");
            if (string.IsNullOrWhiteSpace(model.Reason))
                return new(BookCopyUpdateStatus.ReasonRequired, "Vui lòng nhập lý do khi đổi trạng thái bản sao.");

            dbContext.BookCopyStatusHistories.Add(new BookCopyStatusHistory
            {
                BookCopyId = copy.Id,
                FromStatus = copy.Status,
                ToStatus = model.Status,
                Reason = model.Reason.Trim(),
                ChangedBy = changedBy,
                ChangedAtUtc = DateTime.UtcNow
            });
            copy.Status = model.Status;
            copy.StatusReason = model.Reason.Trim();
        }

        // Mã vạch (CopyCode) cố ý không được gán lại.
        copy.ShelfId = model.ShelfId;
        copy.PhysicalCondition = model.PhysicalCondition;
        copy.Note = string.IsNullOrWhiteSpace(model.Note) ? null : model.Note.Trim();
        await dbContext.SaveChangesAsync(cancellationToken);
        if (statusChanged && model.Status == BookCopyStatus.Available)
            await holdFulfillmentService.FulfillNextAsync(copy.BookId, cancellationToken);
        return new(BookCopyUpdateStatus.Success, Copy: copy);
    }

    public async Task<(int Available, int Total)> CountCopiesAsync(int bookId, CancellationToken cancellationToken = default)
    {
        var statuses = await dbContext.BookCopies.AsNoTracking()
            .Where(copy => copy.BookId == bookId)
            .Select(copy => copy.Status)
            .ToListAsync(cancellationToken);
        return (statuses.Count(status => status == BookCopyStatus.Available), statuses.Count);
    }

    private Task<bool> IsActiveShelfAsync(int shelfId, int? warehouseId, CancellationToken cancellationToken) =>
        dbContext.Shelves.AnyAsync(shelf =>
            shelf.Id == shelfId &&
            shelf.Status == ShelfStatus.Active &&
            shelf.Warehouse!.Status == WarehouseStatus.Active &&
            (warehouseId == null || shelf.WarehouseId == warehouseId), cancellationToken);

    private async Task<int> GetLastBarcodeNumberAsync(CancellationToken cancellationToken)
    {
        var existingCodes = await dbContext.BookCopies.AsNoTracking()
            .Where(copy => copy.CopyCode.Length == BarcodeLength + BarcodePrefix.Length)
            .Select(copy => copy.CopyCode)
            .ToListAsync(cancellationToken);
        var cardCodes = await dbContext.LibraryCards.AsNoTracking()
            .Where(card => card.CardCode.Length == BarcodeLength + BarcodePrefix.Length)
            .Select(card => card.CardCode)
            .ToListAsync(cancellationToken);
        return existingCodes
            .Concat(cardCodes)
            .Where(code => code.StartsWith(BarcodePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(code => int.TryParse(code.AsSpan(BarcodePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0)
            .DefaultIfEmpty(0)
            .Max();
    }

    private static string FormatBarcode(int number) => BarcodePrefix + number.ToString($"D{BarcodeLength}", CultureInfo.InvariantCulture);

    public async Task<BookCopyUpdateResult> AddManualAsync(int bookId, ManualBookCopyViewModel model, CancellationToken cancellationToken = default)
    {
        var errors = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        if (!System.ComponentModel.DataAnnotations.Validator.TryValidateObject(model, new(model), errors, true))
            return new(BookCopyUpdateStatus.InvalidInput, string.Join(" ", errors.Select(error => error.ErrorMessage)));
        if (!BookCopyCondition.All.Contains(model.PhysicalCondition))
            return new(BookCopyUpdateStatus.InvalidCondition, "Tình trạng vật lý không hợp lệ.");
        if (model.CoverPrice != decimal.Round(model.CoverPrice!.Value, 2))
            return new(BookCopyUpdateStatus.InvalidInput, "Giá bìa chỉ được có tối đa 2 chữ số thập phân.");
        if (!await dbContext.Books.AnyAsync(book => book.Id == bookId, cancellationToken))
            return new(BookCopyUpdateStatus.NotFound, "Không tìm thấy đầu sách.");
        if (!await IsActiveShelfAsync(model.ShelfId, model.WarehouseId, cancellationToken))
            return new(BookCopyUpdateStatus.InvalidShelf, "Kệ đã chọn không thuộc kho đã chọn hoặc đã ngừng sử dụng.");

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var code = model.GenerateBarcode ? await NextCopyBarcodeAsync(cancellationToken) : model.CopyCode.Trim();
            if (code is null)
                return new(BookCopyUpdateStatus.InvalidInput, $"Đã hết dãy mã vạch {BarcodePrefix} gồm 6 chữ số ({BarcodePrefix}999999).");
            var duplicate = await FindDuplicateAsync(code, cancellationToken);
            var duplicateCardCode = await dbContext.LibraryCards.AsNoTracking()
                .AnyAsync(card => card.CardCode == code, cancellationToken);
            if (duplicate is not null)
            {
                if (model.GenerateBarcode) continue;
                return duplicate;
            }
            if (duplicateCardCode)
            {
                if (model.GenerateBarcode) continue;
                return new(BookCopyUpdateStatus.DuplicateCode, $"Mã vạch {code} trùng với mã thẻ bạn đọc. Vui lòng nhập mã khác.");
            }
            var copy = new BookCopy
            {
                BookId = bookId, CopyCode = code, ShelfId = model.ShelfId,
                ReceivedDate = model.ReceivedDate, CoverPrice = model.CoverPrice,
                PhysicalCondition = model.PhysicalCondition, Status = BookCopyStatus.Available
            };
            dbContext.BookCopies.Add(copy);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return new(BookCopyUpdateStatus.Success, Copy: copy);
            }
            catch (DbUpdateException)
            {
                // The unique index guards concurrent allocation, including manual LIB entries.
                dbContext.Entry(copy).State = EntityState.Detached;
                duplicate = await FindDuplicateAsync(code, cancellationToken);
                if (duplicate is null) throw;
                if (!model.GenerateBarcode) return duplicate;
            }
        }
        return new(BookCopyUpdateStatus.InvalidInput, "Chưa thể cấp mã vạch do có nhiều yêu cầu đồng thời. Vui lòng thử lại.");
    }

    private async Task<string?> NextCopyBarcodeAsync(CancellationToken cancellationToken)
    {
        var codes = await dbContext.BookCopies.AsNoTracking()
            .Where(copy => copy.CopyCode.Length == BarcodePrefix.Length + BarcodeLength)
            .Select(copy => copy.CopyCode).ToListAsync(cancellationToken);
        var cardCodes = await dbContext.LibraryCards.AsNoTracking()
            .Where(card => card.CardCode.Length == BarcodePrefix.Length + BarcodeLength)
            .Select(card => card.CardCode).ToListAsync(cancellationToken);
        var largest = codes.Concat(cardCodes).Where(code =>
                code.StartsWith(BarcodePrefix, StringComparison.OrdinalIgnoreCase) &&
                code.AsSpan(BarcodePrefix.Length).ToArray().All(character => character is >= '0' and <= '9'))
            .Select(code => int.Parse(code.AsSpan(BarcodePrefix.Length), CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0).Max();
        return largest >= MaximumBarcodeNumber ? null : BarcodePrefix + (largest + 1).ToString($"D{BarcodeLength}", CultureInfo.InvariantCulture);
    }

    private async Task<BookCopyUpdateResult?> FindDuplicateAsync(string code, CancellationToken cancellationToken)
    {
        var existing = await dbContext.BookCopies.AsNoTracking().Include(copy => copy.Book)
            .FirstOrDefaultAsync(copy => copy.CopyCode == code, cancellationToken);
        return existing is null ? null : new(BookCopyUpdateStatus.DuplicateCode,
            $"Mã vạch {code} đang được bản sao #{existing.Id} của đầu sách \"{existing.Book?.Title}\" (#{existing.BookId}) sử dụng.", existing);
    }
}
