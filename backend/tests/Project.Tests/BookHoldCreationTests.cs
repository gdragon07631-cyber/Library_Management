using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class BookHoldCreationTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;
    private readonly ReaderRegistrationService service;

    public BookHoldCreationTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        service = new ReaderRegistrationService(db, new PasswordHasher<ReaderAccount>(),
            NullLogger<ReaderRegistrationService>.Instance);
    }

    [Fact]
    public async Task EligibleReaderCanPlaceBookHoldAndGetsFifoPosition()
    {
        var book = await AddBookAsync();
        var first = await AddReaderAsync("first@example.com", DateOnly.FromDateTime(DateTime.Today.AddDays(30)));
        var next = await AddReaderAsync("next@example.com", DateOnly.FromDateTime(DateTime.Today.AddDays(30)));

        var firstOutcome = await service.HoldDocumentAsync(first.Id, book.Id);
        var nextOutcome = await service.HoldDocumentAsync(next.Id, book.Id);

        Assert.True(firstOutcome.IsAllowed);
        Assert.Equal(1, firstOutcome.QueuePosition);
        Assert.True(nextOutcome.IsAllowed);
        Assert.Equal(2, nextOutcome.QueuePosition);
        var holds = await db.BookHolds.OrderBy(hold => hold.HeldAtUtc).ThenBy(hold => hold.Id).ToListAsync();
        Assert.Equal(new[] { first.Id, next.Id }, holds.Select(hold => hold.ReaderAccountId));
        Assert.All(holds, hold => Assert.Equal(BookHoldStatus.Waiting, hold.Status));
    }

    [Fact]
    public async Task ExpiredCardPreventsHoldWithSpecificReason()
    {
        var book = await AddBookAsync();
        var reader = await AddReaderAsync("expired@example.com", DateOnly.FromDateTime(DateTime.Today.AddDays(-1)));

        var outcome = await service.HoldDocumentAsync(reader.Id, book.Id);

        Assert.False(outcome.IsAllowed);
        Assert.Contains("hết hạn", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await db.BookHolds.ToListAsync());
    }

    [Fact]
    public async Task LockedCardPreventsHoldWithoutCreatingARequest()
    {
        var book = await AddBookAsync();
        var reader = await AddReaderAsync("locked-card@example.com", DateOnly.FromDateTime(DateTime.Today.AddDays(30)));
        reader.LibraryCard!.Status = "Đang bị khoá";
        reader.LibraryCard.LockReason = "Vi phạm nội quy";
        reader.LibraryCard.LockedAtUtc = DateTime.UtcNow;
        reader.LibraryCard.LockExpiresAtUtc = DateTime.UtcNow.AddHours(2);
        await db.SaveChangesAsync();

        var outcome = await service.HoldDocumentAsync(reader.Id, book.Id);

        Assert.False(outcome.IsAllowed);
        Assert.Contains("Vi phạm nội quy", outcome.Message);
        Assert.Contains("Hạn khoá", outcome.Message);
        Assert.Empty(await db.BookHolds.ToListAsync());
    }

    [Fact]
    public async Task ExpiredTimedCardLockAllowsHoldAgain()
    {
        var book = await AddBookAsync();
        var reader = await AddReaderAsync("expired-card-lock@example.com", DateOnly.FromDateTime(DateTime.Today.AddDays(30)));
        reader.LibraryCard!.Status = "Đang bị khoá";
        reader.LibraryCard.LockReason = "Khoá có thời hạn";
        reader.LibraryCard.LockedAtUtc = DateTime.UtcNow.AddDays(-2);
        reader.LibraryCard.LockExpiresAtUtc = DateTime.UtcNow.AddDays(-1);
        reader.LibraryCard.IsLockUntilManualUnlock = false;
        var shelf = await AddShelfAsync();
        db.BookCopies.Add(new BookCopy
        {
            BookId = book.Id,
            ShelfId = shelf.Id,
            CopyCode = "COPY-EXPIRED-LOCK",
            Status = BookCopyStatus.Available
        });
        await db.SaveChangesAsync();

        var outcome = await service.HoldDocumentAsync(reader.Id, book.Id);

        Assert.True(outcome.IsAllowed);
        Assert.Single(await db.BookHolds.Where(hold => hold.ReaderAccountId == reader.Id).ToListAsync());
    }

    [Fact]
    public async Task LockedAccountPreventsHoldWithSpecificReason()
    {
        var book = await AddBookAsync();
        var reader = await AddReaderAsync("locked@example.com", DateOnly.FromDateTime(DateTime.Today.AddDays(30)), "Bị khóa");

        var outcome = await service.HoldDocumentAsync(reader.Id, book.Id);

        Assert.False(outcome.IsAllowed);
        Assert.Contains("khóa", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await db.BookHolds.ToListAsync());
    }

    [Fact]
    public async Task MissingCardPreventsHold()
    {
        var book = await AddBookAsync();
        var reader = new ReaderAccount
        {
            FullName = "No card", DateOfBirth = new DateOnly(2000, 1, 1), Email = "nocard@example.com",
            PhoneNumber = "0900000000", StudentOrStaffCode = "NO-CARD", PasswordHash = "hash", Status = "Đang hoạt động"
        };
        db.ReaderAccounts.Add(reader);
        await db.SaveChangesAsync();

        var outcome = await service.HoldDocumentAsync(reader.Id, book.Id);

        Assert.False(outcome.IsAllowed);
        Assert.Contains("thẻ", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await db.BookHolds.ToListAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReaderMayPlaceHoldWhenFewerThanThreeAreActive(int existingActiveCount)
    {
        var reader = await AddReaderAsync($"limit-{existingActiveCount}@example.com", DateOnly.FromDateTime(DateTime.Today.AddDays(30)));
        for (var index = 0; index < existingActiveCount; index++)
        {
            Assert.True((await service.HoldDocumentAsync(reader.Id, (await AddBookAsync()).Id)).IsAllowed);
            if (index == 0)
            {
                var firstHold = await db.BookHolds.SingleAsync(hold => hold.ReaderAccountId == reader.Id);
                firstHold.Status = BookHoldStatus.Available;
                await db.SaveChangesAsync();
            }
        }

        var result = await service.HoldDocumentAsync(reader.Id, (await AddBookAsync()).Id);

        Assert.True(result.IsAllowed);
        Assert.Equal(existingActiveCount + 1, await db.BookHolds.CountAsync(hold =>
            hold.ReaderAccountId == reader.Id && BookHoldStatus.ActiveStatuses.Contains(hold.Status)));
    }

    [Fact]
    public async Task FourthActiveHoldIsRejectedWithoutCreatingARequest()
    {
        var reader = await AddReaderAsync("limit-full@example.com", DateOnly.FromDateTime(DateTime.Today.AddDays(30)));
        for (var index = 0; index < ReaderRegistrationService.MaximumActiveBookHolds; index++)
            Assert.True((await service.HoldDocumentAsync(reader.Id, (await AddBookAsync()).Id)).IsAllowed);
        var fourthBook = await AddBookAsync();

        var result = await service.HoldDocumentAsync(reader.Id, fourthBook.Id);

        Assert.False(result.IsAllowed);
        Assert.Contains("3", result.Message);
        Assert.False(await db.BookHolds.AnyAsync(hold => hold.ReaderAccountId == reader.Id && hold.BookId == fourthBook.Id));
        Assert.Equal(3, await db.BookHolds.CountAsync(hold =>
            hold.ReaderAccountId == reader.Id && BookHoldStatus.ActiveStatuses.Contains(hold.Status)));
    }

    [Fact]
    public async Task OpenLoanForRequestedBookBlocksHoldWithoutChangingQueueOrCopy()
    {
        var book = await AddBookAsync();
        var reader = await AddReaderAsync("loan-same-book@example.com", DateOnly.FromDateTime(DateTime.Today.AddDays(30)));
        var queuedReader = await AddReaderAsync("loan-queued@example.com", DateOnly.FromDateTime(DateTime.Today.AddDays(30)));
        Assert.True((await service.HoldDocumentAsync(queuedReader.Id, book.Id)).IsAllowed);
        var existingHold = await db.BookHolds.SingleAsync();
        var shelf = await AddShelfAsync();
        var copy = new BookCopy { BookId = book.Id, ShelfId = shelf.Id, CopyCode = "LOAN-COPY", Status = BookCopyStatus.Available };
        db.BookCopies.Add(copy);
        db.BookLoans.Add(CreateLoan(reader.Id, book.Id));
        await db.SaveChangesAsync();

        var outcome = await service.HoldDocumentAsync(reader.Id, book.Id);

        Assert.False(outcome.IsAllowed);
        Assert.Contains("\u0111ang m\u01b0\u1ee3n \u0111\u1ea7u s\u00e1ch n\u00e0y", outcome.Message, StringComparison.OrdinalIgnoreCase);
        var onlyHold = await db.BookHolds.SingleAsync();
        Assert.Equal(existingHold.Id, onlyHold.Id);
        Assert.Equal(BookHoldStatus.Waiting, onlyHold.Status);
        Assert.Equal(BookCopyStatus.Available, (await db.BookCopies.SingleAsync()).Status);
    }

    [Fact]
    public async Task OpenLoanForDifferentBookDoesNotBlockHold()
    {
        var borrowedBook = await AddBookAsync();
        var requestedBook = await AddBookAsync();
        var reader = await AddReaderAsync("loan-other-book@example.com", DateOnly.FromDateTime(DateTime.Today.AddDays(30)));
        db.BookLoans.Add(CreateLoan(reader.Id, borrowedBook.Id));
        await db.SaveChangesAsync();

        var outcome = await service.HoldDocumentAsync(reader.Id, requestedBook.Id);

        Assert.True(outcome.IsAllowed);
        Assert.Single(await db.BookHolds.Where(hold => hold.ReaderAccountId == reader.Id && hold.BookId == requestedBook.Id).ToListAsync());
    }

    [Fact]
    public async Task NoCurrentLoanRecordAllowsPlacingHoldAfterReturn()
    {
        var book = await AddBookAsync();
        var reader = await AddReaderAsync("loan-returned@example.com", DateOnly.FromDateTime(DateTime.Today.AddDays(30)));
        var returnedLoan = CreateLoan(reader.Id, book.Id);
        db.BookLoans.Add(returnedLoan);
        await db.SaveChangesAsync();
        db.BookLoans.Remove(returnedLoan);
        await db.SaveChangesAsync();

        Assert.True((await service.HoldDocumentAsync(reader.Id, book.Id)).IsAllowed);
    }

    [Fact]
    public async Task ActiveDuplicateIsRejectedButCancelledAndCompletedHistoryCanBeReplaced()
    {
        var reader = await AddReaderAsync("history@example.com", DateOnly.FromDateTime(DateTime.Today.AddDays(30)));
        var book = await AddBookAsync();
        Assert.True((await service.HoldDocumentAsync(reader.Id, book.Id)).IsAllowed);
        var current = await db.BookHolds.SingleAsync();
        current.Status = BookHoldStatus.Available;
        await db.SaveChangesAsync();

        var duplicate = await service.HoldDocumentAsync(reader.Id, book.Id);
        Assert.False(duplicate.IsAllowed);
        Assert.Contains("hiệu lực", duplicate.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(await db.BookHolds.ToListAsync());

        current.Status = BookHoldStatus.Cancelled;
        await db.SaveChangesAsync();
        Assert.True((await service.HoldDocumentAsync(reader.Id, book.Id)).IsAllowed);

        var replacement = await db.BookHolds.SingleAsync(hold => hold.Status == BookHoldStatus.Waiting);
        replacement.Status = BookHoldStatus.ConvertedToLoan;
        await db.SaveChangesAsync();
        Assert.True((await service.HoldDocumentAsync(reader.Id, book.Id)).IsAllowed);

        Assert.Equal(3, await db.BookHolds.CountAsync(hold => hold.ReaderAccountId == reader.Id));
        Assert.Single(await db.BookHolds.Where(hold => hold.ReaderAccountId == reader.Id &&
            hold.BookId == book.Id && BookHoldStatus.ActiveStatuses.Contains(hold.Status)).ToListAsync());
    }

    [Fact]
    public async Task AvailableCopyIsHeldForTheFirstReaderWithThreeOpenDayDeadline()
    {
        var book = await AddBookAsync();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var nextDay = today.AddDays(1);
        await new WorkingScheduleService(db).GetWeeklySchedulesAsync();
        var nextDaySchedule = await db.WeeklyWorkingSchedules.SingleAsync(item => item.DayOfWeek == nextDay.DayOfWeek);
        nextDaySchedule.IsOpen = false;
        db.HolidayClosures.Add(new HolidayClosure { HolidayDate = today.AddDays(2), Reason = "Closed" });
        await db.SaveChangesAsync();
        var reader = await AddReaderAsync("ready@example.com", today.AddDays(30));
        var shelf = await AddShelfAsync();
        var copy = new BookCopy { BookId = book.Id, ShelfId = shelf.Id, CopyCode = "COPY-1", Status = BookCopyStatus.Available };
        db.BookCopies.Add(copy);
        await db.SaveChangesAsync();

        var outcome = await service.HoldDocumentAsync(reader.Id, book.Id);

        Assert.True(outcome.IsReserved);
        Assert.Equal(copy.CopyCode, outcome.CopyCode);
        var hold = await db.BookHolds.SingleAsync();
        Assert.Equal(BookHoldStatus.Available, hold.Status);
        Assert.Equal(copy.Id, hold.BookCopyId);
        Assert.Equal(DateTime.Now.Date.AddDays(4), DateTime.SpecifyKind(hold.PickupDeadlineUtc!.Value, DateTimeKind.Utc).ToLocalTime().Date);
        Assert.Equal(BookCopyStatus.OnHold, (await db.BookCopies.SingleAsync()).Status);
        var readerItem = Assert.Single(await service.GetReaderHoldsAsync(reader.Id));
        Assert.Equal(copy.CopyCode, readerItem.CopyBarcode);
        Assert.Equal(hold.PickupDeadlineUtc, readerItem.PickupDeadlineUtc);
        var history = await db.BookCopyStatusHistories.SingleAsync();
        Assert.Equal(BookCopyStatus.Available, history.FromStatus);
        Assert.Equal(BookCopyStatus.OnHold, history.ToStatus);
    }

    [Fact]
    public async Task NewlyAddedCopyServesOnlyTheHeadAndLeavesNextReaderWaiting()
    {
        var book = await AddBookAsync();
        var first = await AddReaderAsync("first-ready@example.com", DateOnly.FromDateTime(DateTime.Today.AddDays(30)));
        var second = await AddReaderAsync("second-ready@example.com", DateOnly.FromDateTime(DateTime.Today.AddDays(30)));
        await service.HoldDocumentAsync(first.Id, book.Id);
        await service.HoldDocumentAsync(second.Id, book.Id);
        var shelf = await AddShelfAsync();
        var copyService = new BookCopyService(db);

        var addResult = await copyService.AddAsync(book.Id, new NewBookCopyViewModel { CopyCode = "COPY-2", ShelfId = shelf.Id });

        Assert.Equal(BookCopyUpdateStatus.Success, addResult.Status);
        var holds = await db.BookHolds.OrderBy(hold => hold.HeldAtUtc).ThenBy(hold => hold.Id).ToListAsync();
        Assert.Equal(BookHoldStatus.Available, holds[0].Status);
        Assert.NotNull(holds[0].BookCopyId);
        Assert.Equal(BookHoldStatus.Waiting, holds[1].Status);

        var nextCopy = await copyService.AddAsync(book.Id, new NewBookCopyViewModel { CopyCode = "COPY-3", ShelfId = shelf.Id });
        Assert.Equal(BookCopyUpdateStatus.Success, nextCopy.Status);
        Assert.Equal(BookHoldStatus.Waiting, (await db.BookHolds.SingleAsync(hold => hold.Id == holds[1].Id)).Status);

        var reservedCopyId = holds[0].BookCopyId;
        var cancel = await new StaffHoldCancellationService(db).CancelAsync(holds[0].Id, "Reader no longer needs it", 1);
        Assert.True(cancel.IsSuccess);
        var promoted = await db.BookHolds.SingleAsync(hold => hold.Id == holds[1].Id);
        Assert.Equal(BookHoldStatus.Available, promoted.Status);
        Assert.Equal(reservedCopyId, promoted.BookCopyId);
        Assert.Equal(DateTime.Now.Date.AddDays(2), DateTime.SpecifyKind(promoted.PickupDeadlineUtc!.Value, DateTimeKind.Utc).ToLocalTime().Date);
    }

    [Fact]
    public async Task NewlyAvailableCopyFulfillsLegacyPromotedHeadWithoutSkippingIt()
    {
        var book = await AddBookAsync();
        var first = await AddReaderAsync("legacy-first@example.com", DateOnly.FromDateTime(DateTime.Today.AddDays(30)));
        var second = await AddReaderAsync("legacy-second@example.com", DateOnly.FromDateTime(DateTime.Today.AddDays(30)));
        await service.HoldDocumentAsync(first.Id, book.Id);
        await service.HoldDocumentAsync(second.Id, book.Id);
        var firstHold = await db.BookHolds.SingleAsync(hold => hold.ReaderAccountId == first.Id);
        Assert.True((await service.CancelReaderHoldAsync(first.Id, firstHold.Id)).Result == BookHoldCancelResult.Success);
        var promoted = await db.BookHolds.SingleAsync(hold => hold.ReaderAccountId == second.Id);
        Assert.Equal(BookHoldStatus.Available, promoted.Status);
        Assert.Null(promoted.BookCopyId);

        var shelf = await AddShelfAsync();
        await new BookCopyService(db).AddAsync(book.Id,
            new NewBookCopyViewModel { CopyCode = "COPY-LEGACY", ShelfId = shelf.Id });

        var assigned = await db.BookHolds.SingleAsync(hold => hold.Id == promoted.Id);
        Assert.Equal(BookHoldStatus.Available, assigned.Status);
        Assert.NotNull(assigned.BookCopyId);
        Assert.Equal(BookCopyStatus.OnHold,
            (await db.BookCopies.SingleAsync(copy => copy.Id == assigned.BookCopyId)).Status);
    }

    private static BookLoan CreateLoan(int readerId, int bookId)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return new BookLoan
        {
            BookId = bookId,
            ReaderAccountId = readerId,
            LoanDate = today,
            OriginalDueDate = today.AddDays(14),
            DueDate = today.AddDays(14)
        };
    }

    private async Task<Book> AddBookAsync()
    {
        var unique = Guid.NewGuid().ToString("N");
        var book = new Book { Title = "Book " + unique, Author = new Author { Name = "Author " + unique } };
        db.Books.Add(book);
        await db.SaveChangesAsync();
        return book;
    }

    private async Task<Shelf> AddShelfAsync()
    {
        var warehouse = new Warehouse { Code = "WH-1", Name = "Warehouse" };
        var shelf = new Shelf { Warehouse = warehouse, Code = Guid.NewGuid().ToString("N"), Name = "Shelf" };
        db.Shelves.Add(shelf);
        await db.SaveChangesAsync();
        return shelf;
    }

    private async Task<ReaderAccount> AddReaderAsync(string email, DateOnly expiresOn, string status = "Đang hoạt động")
    {
        var cardType = await db.LibraryCardTypes.FirstOrDefaultAsync() ?? new LibraryCardType { Name = "Standard" };
        if (cardType.Id == 0)
        {
            db.LibraryCardTypes.Add(cardType);
            await db.SaveChangesAsync();
        }
        var reader = new ReaderAccount
        {
            FullName = "Reader", DateOfBirth = new DateOnly(2000, 1, 1), Email = email,
            PhoneNumber = "0900000000", StudentOrStaffCode = email, PasswordHash = "hash", Status = status
        };
        reader.LibraryCard = new LibraryCard
        {
            CardCode = "CARD-" + email, LibraryCardTypeId = cardType.Id,
            IssuedOn = DateOnly.FromDateTime(DateTime.Today.AddYears(-1)), ExpiresOn = expiresOn, Status = "Đang hoạt động"
        };
        db.ReaderAccounts.Add(reader);
        await db.SaveChangesAsync();
        return reader;
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }
}
