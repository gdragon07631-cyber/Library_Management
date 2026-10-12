using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class HoldPickupConfirmationTests
{
    [Fact]
    public async Task Confirm_CreatesLoanForReservedCopy_AndUpdatesStatuses()
    {
        await using var fixture = await Fixture.CreateAsync();
        var result = await fixture.Service.ConfirmAsync(fixture.HoldId, "CARD-001", "librarian@example.test", fixture.AdminAccountId);

        Assert.True(result.IsSuccess);
        await using var verify = fixture.CreateContext();
        var hold = await verify.BookHolds.SingleAsync();
        var copy = await verify.BookCopies.SingleAsync();
        var loan = await verify.BookLoans.SingleAsync();
        Assert.Equal(BookHoldStatus.ConvertedToLoan, hold.Status);
        Assert.Equal(BookCopyStatus.OnLoan, copy.Status);
        Assert.Equal(copy.Id, loan.BookCopyId);
        Assert.Equal(fixture.BookId, loan.BookId);
        var expectedLoanDate = DateOnly.FromDateTime(DateTime.UtcNow.ToLocalTime());
        Assert.Equal(expectedLoanDate, loan.LoanDate);
        Assert.Equal(expectedLoanDate.AddDays(14), loan.OriginalDueDate);
        Assert.Equal(loan.OriginalDueDate, loan.DueDate);
        Assert.Equal(fixture.HoldId, loan.SourceBookHoldId);
        Assert.Equal(fixture.AdminAccountId, loan.CreatedByAdminAccountId);
        Assert.Single(await verify.BookCopyStatusHistories.ToListAsync());

        var details = await new BookLoanDetailsService(verify).GetHoldConversionDetailsAsync(loan.Id);
        Assert.NotNull(details);
        Assert.Equal(loan.Id, details.LoanId);
        Assert.Equal(fixture.HoldId, details.HoldId);
        Assert.Equal("COPY-001", details.CopyBarcode);
        Assert.Equal("Test Book", details.BookTitle);
        Assert.Equal("CARD-001", details.LibraryCardCode);
        Assert.Equal("Test Reader", details.ReaderName);
        Assert.Equal("reader@example.test", details.ReaderEmail);
        Assert.Equal("000", details.ReaderPhone);
        Assert.Equal(loan.LoanDate, details.LoanDate);
        Assert.Equal(loan.DueDate, details.DueDate);
        Assert.Equal("Librarian One", details.CreatedByName);
        Assert.Equal("librarian@example.test", details.CreatedByEmail);
        Assert.Equal("Đang mượn", details.LoanStatus);
        Assert.Equal(BookHoldStatus.ConvertedToLoan, details.TransactionStatus);
        var holdHistory = await new BookHoldQueueService(verify)
            .GetQueueForBookAsync(fixture.BookId, BookHoldQueueFilter.All);
        Assert.Equal(loan.Id, Assert.Single(holdHistory).LoanId);
        Assert.Null(await new BookLoanDetailsService(verify).GetHoldConversionDetailsAsync(long.MaxValue));

        loan.SourceBookHoldId = null;
        loan.CreatedByAdminAccountId = null;
        await verify.SaveChangesAsync();
        var legacyDetails = await new BookLoanDetailsService(verify).GetHoldConversionDetailsAsync(loan.Id);
        Assert.NotNull(legacyDetails);
        Assert.Equal(fixture.HoldId, legacyDetails.HoldId);
        Assert.Equal("librarian@example.test", legacyDetails.CreatedByName);
        var legacyHoldHistory = await new BookHoldQueueService(verify)
            .GetQueueForBookAsync(fixture.BookId, BookHoldQueueFilter.All);
        Assert.Equal(loan.Id, Assert.Single(legacyHoldHistory).LoanId);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(21)]
    public async Task Confirm_UsesLoanDaysConfiguredForCardType(int loanDays)
    {
        await using var fixture = await Fixture.CreateAsync(loanDays: loanDays);
        var result = await fixture.Service.ConfirmAsync(fixture.HoldId, "CARD-001", "librarian", fixture.AdminAccountId);

        Assert.True(result.IsSuccess);
        await using var verify = fixture.CreateContext();
        var loan = await verify.BookLoans.SingleAsync();
        Assert.Equal(loan.LoanDate.AddDays(loanDays), loan.OriginalDueDate);
        Assert.Equal(loan.OriginalDueDate, loan.DueDate);
    }

    [Fact]
    public async Task Confirm_MovesHolidayDueDateToNextOpenDate()
    {
        var loanDate = DateOnly.FromDateTime(DateTime.UtcNow.ToLocalTime());
        var proposedDueDate = loanDate.AddDays(8);
        await using var fixture = await Fixture.CreateAsync(loanDays: 8, holidayDates: [proposedDueDate]);

        var result = await fixture.Service.ConfirmAsync(fixture.HoldId, "CARD-001", "librarian", fixture.AdminAccountId);

        Assert.True(result.IsSuccess);
        await using var verify = fixture.CreateContext();
        var loan = await verify.BookLoans.SingleAsync();
        Assert.Equal(proposedDueDate, loan.OriginalDueDate);
        Assert.Equal(proposedDueDate.AddDays(1), loan.DueDate);
    }

    [Fact]
    public async Task Confirm_SkipsMultipleConsecutiveClosedDates()
    {
        var loanDate = DateOnly.FromDateTime(DateTime.UtcNow.ToLocalTime());
        var proposedDueDate = loanDate.AddDays(8);
        await using var fixture = await Fixture.CreateAsync(
            loanDays: 8,
            holidayDates: [proposedDueDate, proposedDueDate.AddDays(1)],
            closedDay: proposedDueDate.AddDays(2).DayOfWeek);

        var result = await fixture.Service.ConfirmAsync(fixture.HoldId, "CARD-001", "librarian", fixture.AdminAccountId);

        Assert.True(result.IsSuccess);
        await using var verify = fixture.CreateContext();
        var loan = await verify.BookLoans.SingleAsync();
        Assert.Equal(proposedDueDate.AddDays(3), loan.DueDate);
    }

    [Fact]
    public async Task Confirm_RejectsCardTypeWithoutLoanPolicy_WithoutChanges()
    {
        await using var fixture = await Fixture.CreateAsync(loanDays: null);

        var result = await fixture.Service.ConfirmAsync(fixture.HoldId, "CARD-001", "librarian", fixture.AdminAccountId);

        Assert.False(result.IsSuccess);
        Assert.Contains("chưa được cấu hình số ngày mượn", result.Message);
        await fixture.AssertUnchangedAsync();
    }

    [Fact]
    public async Task Confirm_RejectsIncompleteWeeklyCalendar_WithoutChanges()
    {
        await using var fixture = await Fixture.CreateAsync(includeSchedules: false);

        var result = await fixture.Service.ConfirmAsync(fixture.HoldId, "CARD-001", "librarian", fixture.AdminAccountId);

        Assert.False(result.IsSuccess);
        Assert.Contains("chưa được cấu hình đầy đủ", result.Message);
        await fixture.AssertUnchangedAsync();
    }

    [Fact]
    public async Task Confirm_RejectsHoldThatExpiredMomentsAgo_AndRequestsReapplication()
    {
        await using var fixture = await Fixture.CreateAsync(pickupDeadlineUtc: DateTime.UtcNow.AddSeconds(-2));

        var result = await fixture.Service.ConfirmAsync(fixture.HoldId, "CARD-001", "librarian", fixture.AdminAccountId);

        Assert.False(result.IsSuccess);
        Assert.Contains("quá hạn nhận", result.Message);
        Assert.Contains("đặt lại đơn", result.Message);
        await fixture.AssertUnchangedAsync();
    }

    [Fact]
    public async Task Confirm_RejectsHoldExpiredForSeveralDaysWithoutChangingCopy()
    {
        await using var fixture = await Fixture.CreateAsync(pickupDeadlineUtc: DateTime.UtcNow.AddDays(-7));

        var result = await fixture.Service.ConfirmAsync(fixture.HoldId, "CARD-001", "librarian", fixture.AdminAccountId);

        Assert.False(result.IsSuccess);
        Assert.Contains("đặt lại đơn", result.Message);
        await fixture.AssertUnchangedAsync();
    }

    [Fact]
    public void PickupViewModel_LabelsOverdueWaitingHold()
    {
        var item = new HoldPickupItemViewModel
        {
            Status = BookHoldStatus.Available,
            PickupDeadlineUtc = DateTime.UtcNow.AddMinutes(-1)
        };

        Assert.True(item.IsPickupExpired);
        Assert.Equal("Quá hạn nhận", item.DisplayStatus);
        Assert.False(item.CanConfirm);
    }

    [Fact]
    public void LoanDetailsController_IsRestrictedToLibraryStaffRoles()
    {
        var staffOnly = (Project.Filters.StaffOnlyAttribute?)Attribute.GetCustomAttribute(
            typeof(Project.Controllers.BookLoanDetailsController), typeof(Project.Filters.StaffOnlyAttribute));

        Assert.NotNull(staffOnly);
        Assert.Contains(AccountRoles.Librarian, staffOnly.Roles);
        Assert.Contains(AccountRoles.SystemAdmin, staffOnly.Roles);
        Assert.Contains(AccountRoles.LibraryManager, staffOnly.Roles);
    }

    [Fact]
    public async Task Confirm_RejectsMismatchedCard_WithoutChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        var result = await fixture.Service.ConfirmAsync(fixture.HoldId, "CARD-WRONG", "librarian", fixture.AdminAccountId);

        Assert.False(result.IsSuccess);
        Assert.Contains("không khớp", result.Message);
        await fixture.AssertUnchangedAsync();
    }

    [Fact]
    public async Task Confirm_RejectsAlreadyConvertedHold()
    {
        await using var fixture = await Fixture.CreateAsync(BookHoldStatus.ConvertedToLoan);
        var result = await fixture.Service.ConfirmAsync(fixture.HoldId, "CARD-001", "librarian", fixture.AdminAccountId);

        Assert.False(result.IsSuccess);
        Assert.Contains("đã được chuyển", result.Message);
        await fixture.AssertUnchangedAsync(BookHoldStatus.ConvertedToLoan);
    }

    [Fact]
    public async Task Confirm_RollsBackLoanAndStatuses_WhenFailureOccursMidTransaction()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var command = fixture.Connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER fail_history BEFORE INSERT ON BookCopyStatusHistories BEGIN SELECT RAISE(ABORT, 'forced failure'); END;";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Service.ConfirmAsync(fixture.HoldId, "CARD-001", "librarian", fixture.AdminAccountId));
        await fixture.AssertUnchangedAsync();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public SqliteConnection Connection { get; }
        public ApplicationDbContext Context { get; }
        public HoldPickupConfirmationService Service { get; }
        public long HoldId { get; private init; }
        public int BookId { get; private init; }
        public int AdminAccountId { get; private init; }

        private Fixture(SqliteConnection connection, ApplicationDbContext context, long holdId, int bookId, int adminAccountId)
        {
            Connection = connection;
            Context = context;
            HoldId = holdId;
            BookId = bookId;
            AdminAccountId = adminAccountId;
            Service = new HoldPickupConfirmationService(context, new CreatingLoanService(context), new WorkingScheduleService(context));
        }

        public static async Task<Fixture> CreateAsync(
            string holdStatus = "Đã có sách", int? loanDays = 14,
            IEnumerable<DateOnly>? holidayDates = null, DayOfWeek? closedDay = null, bool includeSchedules = true,
            DateTime? pickupDeadlineUtc = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
            var context = new ApplicationDbContext(options);
            await context.Database.EnsureCreatedAsync();
            var admin = new AdminAccount
            {
                FullName = "Librarian One", Email = "librarian@example.test", PasswordHash = "hash", Role = AccountRoles.Librarian
            };
            context.AdminAccounts.Add(admin);
            if (includeSchedules)
            {
                context.WeeklyWorkingSchedules.AddRange(Enum.GetValues<DayOfWeek>().Select(day =>
                    new WeeklyWorkingSchedule { DayOfWeek = day, IsOpen = day != closedDay }));
            }
            if (holidayDates != null)
            {
                context.HolidayClosures.AddRange(holidayDates.Select(date => new HolidayClosure { HolidayDate = date, Reason = "Test closure" }));
            }
            await context.SaveChangesAsync();
            var author = new Author { Name = "Test Author" };
            context.Authors.Add(author);
            var reader = new ReaderAccount
            {
                FullName = "Test Reader", DateOfBirth = new DateOnly(2000, 1, 1), Email = "reader@example.test",
                PhoneNumber = "000", StudentOrStaffCode = "ST-001", PasswordHash = "hash", Status = "Đang hoạt động"
            };
            var cardType = new LibraryCardType { Name = "Sinh viên", LoanDays = loanDays };
            context.ReaderAccounts.Add(reader);
            context.LibraryCardTypes.Add(cardType);
            var warehouse = new Warehouse { Code = "WH", Name = "Warehouse" };
            context.Warehouses.Add(warehouse);
            await context.SaveChangesAsync();
            var card = new LibraryCard
            {
                CardCode = "CARD-001", ReaderAccountId = reader.Id, LibraryCardTypeId = cardType.Id,
                IssuedOn = new DateOnly(2025, 1, 1), ExpiresOn = new DateOnly(2030, 1, 1)
            };
            var shelf = new Shelf { WarehouseId = warehouse.Id, Code = "S1", Name = "Shelf" };
            var book = new Book { Title = "Test Book", AuthorId = author.Id };
            context.LibraryCards.Add(card);
            context.Shelves.Add(shelf);
            context.Books.Add(book);
            await context.SaveChangesAsync();
            var copy = new BookCopy { BookId = book.Id, ShelfId = shelf.Id, CopyCode = "COPY-001", Status = BookCopyStatus.OnHold };
            context.BookCopies.Add(copy);
            await context.SaveChangesAsync();
            var hold = new BookHold
            {
                ReaderAccountId = reader.Id, BookId = book.Id, BookCopyId = copy.Id,
                Status = holdStatus, HeldAtUtc = DateTime.UtcNow,
                PickupDeadlineUtc = pickupDeadlineUtc ?? DateTime.UtcNow.AddDays(2)
            };
            context.BookHolds.Add(hold);
            await context.SaveChangesAsync();
            return new Fixture(connection, context, hold.Id, book.Id, admin.Id);
        }

        public ApplicationDbContext CreateContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(Connection).Options);

        public async Task AssertUnchangedAsync(string expectedHoldStatus = "Đã có sách")
        {
            await using var verify = CreateContext();
            Assert.Empty(await verify.BookLoans.ToListAsync());
            Assert.Equal(expectedHoldStatus, await verify.BookHolds.Select(hold => hold.Status).SingleAsync());
            Assert.Equal(BookCopyStatus.OnHold, await verify.BookCopies.Select(copy => copy.Status).SingleAsync());
            Assert.Empty(await verify.BookCopyStatusHistories.ToListAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class CreatingLoanService(ApplicationDbContext db) : IBookLoanService
    {
        public async Task<IReadOnlyList<BookLoan>> GetForReaderAsync(int readerAccountId, CancellationToken ct = default) =>
            await db.BookLoans.Where(item => item.ReaderAccountId == readerAccountId).ToListAsync(ct);
        public Task<RenewBookLoanOutcome> RenewForReaderAsync(long id, int readerAccountId, DateOnly today, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<OverdueLoanItem>> GetOverdueAsync(DateOnly today, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<OverdueLoanItem>>([]);
        public Task<IReadOnlyList<OverdueLoanItem>> GetOverdueAsync(DateOnly today, OverdueLoanRange range, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<OverdueLoanItem>>([]);
        public Task<IReadOnlyList<BookLoan>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BookLoanOutcome> CreateAsync(int bookId, int readerAccountId, DateOnly loanDate, CancellationToken cancellationToken = default) => CreateAsync(bookId, readerAccountId, loanDate, null, cancellationToken);
        public async Task<BookLoanOutcome> CreateAsync(int bookId, int readerAccountId, DateOnly loanDate, string? actor, CancellationToken cancellationToken = default)
        {
            var loan = new BookLoan { BookId = bookId, ReaderAccountId = readerAccountId, LoanDate = loanDate, OriginalDueDate = loanDate.AddDays(14), DueDate = loanDate.AddDays(14), CreatedAtUtc = DateTime.UtcNow };
            db.BookLoans.Add(loan);
            await db.SaveChangesAsync(cancellationToken);
            return new(true, Loan: loan);
        }
        public async Task<BookLoanOutcome> CreateForHoldAsync(int bookId, int readerAccountId, DateOnly loanDate, DateOnly originalDueDate, DateOnly dueDate, string? actor, CancellationToken cancellationToken = default)
        {
            var loan = new BookLoan { BookId = bookId, ReaderAccountId = readerAccountId, LoanDate = loanDate, OriginalDueDate = originalDueDate, DueDate = dueDate, CreatedAtUtc = DateTime.UtcNow };
            db.BookLoans.Add(loan);
            await db.SaveChangesAsync(cancellationToken);
            return new(true, Loan: loan);
        }
        public Task<BatchBookLoanOutcome> CreateManyAsync(IReadOnlyList<int> ids, int readerId, DateOnly date, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BatchBookLoanOutcome> CreateManyAsync(IReadOnlyList<int> ids, int readerId, DateOnly date, string? actor, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RenewBookLoanOutcome> RenewAsync(long id, DateOnly today, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DateOnly> AdjustDueDateAsync(DateOnly date, CancellationToken ct = default) => Task.FromResult(date);
        public Task<IReadOnlyList<BlockedLoanLogEntry>> GetBlockedLoanLogsAsync(int? readerId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BookLoanOutcome> OverrideCreateAsync(int bookId, int readerId, DateOnly date, string actor, string reason, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BatchBookLoanOutcome> OverrideCreateManyAsync(IReadOnlyList<int> ids, int readerId, DateOnly date, string actor, string reason, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ReturnBookLoanOutcome> ReturnAsync(long id, DateTime returnedAtUtc, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BookCopyLoanLookupOutcome> LookupByCopyCodeAsync(string copyCode, DateOnly today, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
