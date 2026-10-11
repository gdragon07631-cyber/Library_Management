using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed partial class DirectLoanTests
{
    private async Task<int> OtherReaderAsync()
    {
        var existing = await db.LibraryCards.SingleOrDefaultAsync(c => c.CardCode == "CARD02");
        if (existing != null) return existing.ReaderAccountId;
        var reader = new ReaderAccount { FullName = "Tên người giữ riêng tư", Email = "private@test.local",
            PasswordHash = "test", StudentOrStaffCode = "SV02", PhoneNumber = "0998765432", Status = "Đang hoạt động" };
        db.LibraryCards.Add(new LibraryCard { CardCode = "CARD02", ReaderAccount = reader,
            LibraryCardTypeId = (await db.LibraryCards.SingleAsync(c => c.CardCode == "CARD01")).LibraryCardTypeId,
            IssuedOn = today.AddDays(-1), ExpiresOn = today.AddYears(1) });
        await db.SaveChangesAsync();
        return reader.Id;
    }

    private async Task<BookHold> AddHoldAsync(string code, bool other = false)
    {
        var readerId = other ? await OtherReaderAsync() : (await db.LibraryCards.SingleAsync(c => c.CardCode == "CARD01")).ReaderAccountId;
        var copy = await db.BookCopies.SingleAsync(c => c.CopyCode == code);
        var hold = new BookHold { BookId = copy.BookId, BookCopyId = copy.Id, ReaderAccountId = readerId,
            Status = BookHoldStatus.Available, PickupDeadlineUtc = DateTime.UtcNow.AddHours(1) };
        copy.Status = BookCopyStatus.OnHold;
        db.BookHolds.Add(hold);
        await db.SaveChangesAsync();
        return hold;
    }

    [Theory]
    [InlineData(BookCopyStatus.OnHold)]
    [InlineData(BookCopyStatus.Available)]
    public async Task OtherReadersHoldBlocksBatchEvenIfCopyStatusIsInconsistent(string copyStatus)
    {
        var codes = await AddCopies(3);
        var hold = await AddHoldAsync("COPY02", other: true);
        (await db.BookCopies.SingleAsync(c => c.CopyCode == "COPY02")).Status = copyStatus;
        await db.SaveChangesAsync();
        var result = await service.ConfirmManyAsync("CARD01", codes, staff, rowIds: ["one", "held", "three"]);
        Assert.False(result.IsSuccess);
        var blocked = Assert.Single(result.Rows!, r => !r.IsValid);
        Assert.Equal("held", blocked.RowId);
        Assert.Equal(hold.Id, blocked.HoldId);
        Assert.Contains($"#{hold.Id}", blocked.Error!);
        Assert.Contains("của bạn đọc khác", blocked.Error!);
        Assert.Empty(await db.BookLoans.ToListAsync());
        Assert.Equal(BookHoldStatus.Available, (await db.BookHolds.SingleAsync()).Status);
        // Removing the rejected row succeeds without changing the other reader's reservation.
        Assert.True((await service.ConfirmManyAsync("CARD01", ["COPY01", "COPY03"], staff)).IsSuccess);
        Assert.Equal(2, await db.BookLoans.CountAsync());
        Assert.Equal(copyStatus, (await db.BookCopies.SingleAsync(c => c.CopyCode == "COPY02")).Status);
        Assert.Equal(BookHoldStatus.Available, (await db.BookHolds.SingleAsync()).Status);
    }

    [Theory]
    [InlineData("Đã có sách", false)]
    [InlineData("Chờ nhận", false)]
    [InlineData("Sẵn sàng nhận", false)]
    [InlineData("WaitingPickup", false)]
    [InlineData("Đã có sách", true)]
    public async Task OwnerCanBorrowHeldAndOrdinaryCopiesTogether(string status, bool noDeadline)
    {
        await AddCopies(2);
        var hold = await AddHoldAsync("COPY02");
        hold.Status = status;
        if (noDeadline) hold.PickupDeadlineUtc = null;
        await db.SaveChangesAsync();
        var page = await service.LookupAsync("CARD01", "COPY02");
        Assert.True(page.CanConfirm, page.CopyError);
        Assert.Equal(hold.Id, page.HoldId);
        var result = await service.ConfirmManyAsync("CARD01", ["COPY01", "COPY02"], staff);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        var linked = await db.BookLoans.SingleAsync(l => l.SourceBookHoldId == hold.Id);
        Assert.NotNull(await service.ReceiptAsync(linked.Id));
        Assert.Equal(BookHoldStatus.ConvertedToLoan, (await db.BookHolds.SingleAsync()).Status);
        Assert.Equal(2, await db.BookCopies.CountAsync(c => c.Status == BookCopyStatus.OnLoan));
        Assert.Equal(2, (await service.LookupAsync("CARD01", null)).CurrentLoans);
        Assert.Contains(await db.BookCopyStatusHistories.ToListAsync(), h => h.FromStatus == BookCopyStatus.OnHold);
        Assert.False((await service.ConfirmAsync("CARD01", "COPY02", staff)).IsSuccess);
        Assert.Equal(2, await db.BookLoans.CountAsync());
    }

    [Theory]
    [InlineData("expired", BookCopyStatus.OnHold, false)]
    [InlineData("expired", BookCopyStatus.Available, true)]
    [InlineData("cancelled", BookCopyStatus.Available, true)]
    [InlineData("converted", BookCopyStatus.Available, true)]
    [InlineData("cancelled", BookCopyStatus.OnHold, false)]
    [InlineData("converted", BookCopyStatus.OnHold, false)]
    public async Task InactiveHoldsDoNotGrantOrRetainBorrowingRights(string scenario, string copyStatus, bool allowed)
    {
        var hold = await AddHoldAsync("COPY01");
        if (scenario == "expired") hold.PickupDeadlineUtc = DateTime.UtcNow.AddMinutes(-1);
        if (scenario == "cancelled") hold.Status = BookHoldStatus.Cancelled;
        if (scenario == "converted") hold.Status = BookHoldStatus.ConvertedToLoan;
        var originalStatus = hold.Status;
        (await db.BookCopies.SingleAsync()).Status = copyStatus;
        await db.SaveChangesAsync();
        var result = await service.ConfirmAsync("CARD01", "COPY01", staff);
        Assert.Equal(allowed, result.IsSuccess);
        Assert.Equal(originalStatus, (await db.BookHolds.SingleAsync()).Status);
        if (allowed) Assert.Null((await db.BookLoans.SingleAsync()).SourceBookHoldId);
        else Assert.Empty(await db.BookLoans.ToListAsync());
    }

    [Theory]
    [InlineData("multiple")]
    [InlineData("wrong-book")]
    [InlineData("wrong-copy-status")]
    public async Task AmbiguousOrInconsistentHoldDataIsRejected(string scenario)
    {
        var hold = await AddHoldAsync("COPY01");
        if (scenario == "multiple") await AddHoldAsync("COPY01", other: true);
        if (scenario == "wrong-book")
        {
            var book = new Book { Title = "Khác", AuthorId = (await db.Books.SingleAsync()).AuthorId };
            db.Books.Add(book); await db.SaveChangesAsync(); hold.BookId = book.Id;
        }
        if (scenario == "wrong-copy-status") (await db.BookCopies.SingleAsync()).Status = BookCopyStatus.Available;
        await db.SaveChangesAsync();
        Assert.False((await service.ConfirmAsync("CARD01", "COPY01", staff)).IsSuccess);
        Assert.Empty(await db.BookLoans.ToListAsync());
        Assert.All(await db.BookHolds.ToListAsync(), h => Assert.Equal(BookHoldStatus.Available, h.Status));
    }

    [Fact]
    public async Task TitleOnlyHoldDoesNotReserveAnUnassignedCopyAtCheckout()
    {
        var hold = await AddHoldAsync("COPY01", other: true);
        hold.BookCopyId = null;
        (await db.BookCopies.SingleAsync()).Status = BookCopyStatus.Available;
        await db.SaveChangesAsync();
        Assert.True((await service.ConfirmAsync("CARD01", "COPY01", staff)).IsSuccess);
        Assert.Null((await db.BookLoans.SingleAsync()).SourceBookHoldId);
        Assert.Null((await db.BookHolds.SingleAsync()).BookCopyId);
    }

    [Theory]
    [InlineData("appeared")]
    [InlineData("owner-changed")]
    [InlineData("expired")]
    [InlineData("cancelled")]
    public async Task ConfirmationRechecksHoldChangesAfterLookup(string change)
    {
        BookHold? hold = change == "appeared" ? null : await AddHoldAsync("COPY01");
        Assert.True((await service.LookupAsync("CARD01", "COPY01")).CanConfirm);
        if (change == "appeared") await AddHoldAsync("COPY01", other: true);
        if (change == "owner-changed") hold!.ReaderAccountId = await OtherReaderAsync();
        if (change == "expired") hold!.PickupDeadlineUtc = DateTime.UtcNow.AddSeconds(-1);
        if (change == "cancelled") hold!.Status = BookHoldStatus.Cancelled;
        await db.SaveChangesAsync();
        var result = await service.ConfirmManyAsync("CARD01", ["COPY01"], staff, rowIds: ["stable-id"]);
        Assert.False(result.IsSuccess);
        Assert.Equal("stable-id", Assert.Single(result.Rows!).RowId);
        Assert.Empty(await db.BookLoans.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterSaveFailureRollsBackHoldAndAllLoanLines(bool wrappedFailure)
    {
        await AddCopies(2);
        var hold = await AddHoldAsync("COPY01");
        failure.FailAt = 4; failure.Enabled = true;
        failure.WrappedFailure = wrappedFailure;
        var result = await service.ConfirmManyAsync("CARD01", ["COPY01", "COPY02"], staff);
        Assert.False(result.IsSuccess);
        Assert.Equal(4, failure.Saves);
        Assert.Equal(BookHoldStatus.Available, (await db.BookHolds.SingleAsync()).Status);
        Assert.Equal(BookCopyStatus.OnHold, (await db.BookCopies.SingleAsync(c => c.CopyCode == "COPY01")).Status);
        Assert.Equal(BookCopyStatus.Available, (await db.BookCopies.SingleAsync(c => c.CopyCode == "COPY02")).Status);
        Assert.Empty(await db.BookLoans.ToListAsync());
        Assert.Empty(await db.BookCopyStatusHistories.ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentOwnerAndOtherReaderNeverIssueToOtherReader()
    {
        await OtherReaderAsync();
        var hold = await AddHoldAsync("COPY01");
        using var barrier = new Barrier(2);
        async Task<BookLoanOutcome> Run(string card) => await Task.Run(async () =>
        {
            using var context = new ApplicationDbContext(Options().Options);
            var schedule = new WorkingScheduleService(context);
            var direct = new DirectLoanService(context, new BookLoanService(context, schedule), schedule, NullLogger<DirectLoanService>.Instance);
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            return await direct.ConfirmAsync(card, "COPY01", staff);
        });
        var results = await Task.WhenAll(Run("CARD01"), Run("CARD02"));
        Assert.False(results[1].IsSuccess);
        Assert.All(await db.BookLoans.AsNoTracking().ToListAsync(), l => Assert.Equal(hold.ReaderAccountId, l.ReaderAccountId));
        if (!results[0].IsSuccess) Assert.True((await service.ConfirmAsync("CARD01", "COPY01", staff)).IsSuccess);
        Assert.Single(await db.BookLoans.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentAllocationCannotLeaveAnIssuedCopyReservedForAnotherReader()
    {
        var otherId = await OtherReaderAsync();
        var copy = await db.BookCopies.SingleAsync();
        db.BookHolds.Add(new BookHold { ReaderAccountId = otherId, BookId = copy.BookId, Status = BookHoldStatus.Waiting });
        await db.SaveChangesAsync();
        using var barrier = new Barrier(2);
        var issue = Task.Run(async () =>
        {
            using var context = new ApplicationDbContext(Options().Options);
            var schedule = new WorkingScheduleService(context);
            var direct = new DirectLoanService(context, new BookLoanService(context, schedule), schedule, NullLogger<DirectLoanService>.Instance);
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            return await direct.ConfirmAsync("CARD01", "COPY01", staff);
        });
        var reserve = Task.Run(async () =>
        {
            using var context = new ApplicationDbContext(Options().Options);
            var allocator = new BookHoldFulfillmentService(context, new BookLoanService(context, new WorkingScheduleService(context)));
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            try { await allocator.FulfillNextAsync(copy.BookId); }
            catch (Exception ex) when (ex is System.Data.Common.DbException or DbUpdateException
                or InvalidOperationException { InnerException: System.Data.Common.DbException }) { /* A deadlock victim must roll back. */ }
        });
        await Task.WhenAll(issue, reserve);
        var current = await db.BookCopies.AsNoTracking().SingleAsync();
        var hold = await db.BookHolds.AsNoTracking().SingleAsync();
        if ((await issue).IsSuccess)
        {
            Assert.Equal(BookCopyStatus.OnLoan, current.Status);
            Assert.Null(hold.BookCopyId);
            Assert.Equal(BookHoldStatus.Waiting, hold.Status);
        }
        else
        {
            Assert.Empty(await db.BookLoans.ToListAsync());
            if (hold.BookCopyId != null) Assert.Equal(BookCopyStatus.OnHold, current.Status);
        }
    }

    [Fact]
    public async Task MultipleOwnHoldsAreConvertedTogetherAndOtherHoldsAreUntouched()
    {
        await AddCopies(3);
        var first = await AddHoldAsync("COPY01");
        var second = await AddHoldAsync("COPY02");
        var other = await AddHoldAsync("COPY03", other: true);
        var result = await service.ConfirmManyAsync("CARD01", ["COPY01", "COPY02"], staff);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(new[] { first.Id, second.Id }, (await db.BookLoans.OrderBy(l => l.SourceBookHoldId).ToListAsync()).Select(l => l.SourceBookHoldId!.Value));
        Assert.Equal(2, await db.BookHolds.CountAsync(h => h.Status == BookHoldStatus.ConvertedToLoan));
        Assert.Equal(BookHoldStatus.Available, (await db.BookHolds.AsNoTracking().SingleAsync(h => h.Id == other.Id)).Status);
    }
}
