using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Project.Controllers;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class BookLoanLimitTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public async Task GetBorrowingStatus_ReportsActualCountAndBlockedState(int borrowedCount, bool isBlocked)
    {
        await using var fixture = await Fixture.CreateAsync(limit: 5, borrowedCount);

        var status = await fixture.Service.GetBorrowingStatusAsync(fixture.Reader.Id);

        Assert.Equal(borrowedCount, status!.BorrowedCount);
        Assert.Equal(5, status.BorrowingLimit);
        Assert.Equal(Math.Max(0, 5 - borrowedCount), status.Remaining);
        Assert.Equal(isBlocked, status.IsBlocked);
    }

    [Fact]
    public async Task Create_UnderLimit_AllowsLoanAndCountsOnlyUnreturnedLoans()
    {
        await using var fixture = await Fixture.CreateAsync(limit: 5, borrowedCount: 3);

        var status = await fixture.Service.GetBorrowingStatusAsync(fixture.Reader.Id);
        var result = await fixture.Service.CreateAsync(fixture.Book.Id, fixture.Reader.Id, DateOnly.FromDateTime(DateTime.Today));

        Assert.Equal(3, status!.BorrowedCount);
        Assert.Equal(5, status.BorrowingLimit);
        Assert.Equal(2, status.Remaining);
        Assert.Equal(0m, status.OutstandingBalance);
        Assert.True(result.IsSuccess);
        Assert.Equal(4, await fixture.Db.BookLoans.CountAsync(loan =>
            loan.ReaderAccountId == fixture.Reader.Id && loan.ReturnedAtUtc == null));
    }

    [Fact]
    public async Task Create_WithOutstandingBalance_BlocksAndWritesDetailedAuditLog()
    {
        await using var fixture = await Fixture.CreateAsync(limit: 5, borrowedCount: 0);
        fixture.Reader.OutstandingBalance = 150000m;
        await fixture.Db.SaveChangesAsync();

        var status = await fixture.Service.GetBorrowingStatusAsync(fixture.Reader.Id);
        var result = await fixture.Service.CreateAsync(
            fixture.Book.Id, fixture.Reader.Id, DateOnly.FromDateTime(DateTime.Today),
            actor: "Thủ thư kiểm thử", ipAddress: "127.0.0.1");

        Assert.Equal(150000m, status!.OutstandingBalance);
        Assert.Equal("150.000", status.OutstandingBalanceFormatted);
        Assert.Contains(status.BlockingReasons, reason => reason.Code == "UNPAID_FEE");
        Assert.False(result.IsSuccess);
        Assert.Equal("Còn nợ 150.000 VND, không thể mượn sách.", result.ErrorMessage);
        Assert.Equal(150000m, result.OutstandingBalance);
        Assert.Empty(await fixture.Db.BookLoans.Where(loan => loan.ReaderAccountId == fixture.Reader.Id).ToListAsync());

        var blockLog = await fixture.Db.AuditBlockLogs.SingleAsync();
        Assert.Equal(fixture.Reader.StudentOrStaffCode, blockLog.ReaderCode);
        Assert.Equal(fixture.Reader.FullName, blockLog.ReaderName);
        Assert.Equal(150000m, blockLog.OutstandingBalance);
        Assert.Equal("Thủ thư kiểm thử", blockLog.Actor);
        Assert.Contains("Còn nợ 150.000 VND", blockLog.Reasons);
        Assert.Equal("UNPAID_FEE", blockLog.BlockingReasonCodes);
        Assert.Equal(LoanBlockHistoryEventTypes.BlockedDebt, blockLog.EventType);
        Assert.NotEqual(default, blockLog.OccurredAtUtc);
    }

    [Fact]
    public async Task CreateApi_WithOutstandingBalance_ReturnsConflictAndDoesNotCreateLoan()
    {
        await using var fixture = await Fixture.CreateAsync(limit: 5, borrowedCount: 0);
        fixture.Reader.OutstandingBalance = 150000m;
        await fixture.Db.SaveChangesAsync();

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        var audit = new AuditLogService(fixture.Db, NullLogger<AuditLogService>.Instance);
        var controller = new LoanController(fixture.Service, fixture.Db, audit)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            ObjectValidator = new NoopObjectModelValidator()
        };

        var result = await controller.CreateApi(new CreateBookLoanViewModel
        {
            BookId = fixture.Book.Id,
            ReaderAccountId = fixture.Reader.Id,
            LoanDate = DateOnly.FromDateTime(DateTime.Today)
        });

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(409, conflict.StatusCode);
        var payload = JsonDocument.Parse(JsonSerializer.Serialize(conflict.Value));
        Assert.Equal("Còn nợ 150.000 VND, không thể mượn sách.",
            payload.RootElement.GetProperty("message").GetString());
        Assert.Equal(150000m,
            payload.RootElement.GetProperty("outstandingBalance").GetDecimal());
        Assert.Equal("150.000",
            payload.RootElement.GetProperty("outstandingBalanceFormatted").GetString());
        Assert.Empty(await fixture.Db.BookLoans.Where(loan => loan.ReaderAccountId == fixture.Reader.Id).ToListAsync());
        Assert.Single(await fixture.Db.AuditBlockLogs.ToListAsync());
    }

    [Fact]
    public async Task CreateApi_LibraryManagerCanOverrideOnce_AndNextLoanIsBlockedAgain()
    {
        await using var fixture = await Fixture.CreateAsync(limit: 1, borrowedCount: 1);
        var requestId = Guid.NewGuid();
        var controller = await CreateLoanControllerAsync(fixture, AccountRoles.LibraryManager);
        var model = new CreateBookLoanViewModel
        {
            BookId = fixture.Book.Id,
            ReaderAccountId = fixture.Reader.Id,
            LoanDate = DateOnly.FromDateTime(DateTime.Today),
            OverrideBlocked = true,
            OverrideReason = "Được quản lý phê duyệt cho trường hợp đặc biệt.",
            OverrideRequestId = requestId.ToString()
        };

        var result = await controller.CreateApi(model);

        Assert.IsType<CreatedResult>(result);
        var exceptionLog = await fixture.Db.AuditBlockLogs.SingleAsync();
        Assert.Equal("Used", exceptionLog.Outcome);
        Assert.Equal(LoanBlockHistoryEventTypes.OverrideUsed, exceptionLog.EventType);
        Assert.Equal(requestId, exceptionLog.OverrideRequestId);
        Assert.Equal("Được quản lý phê duyệt cho trường hợp đặc biệt.", exceptionLog.OverrideReason);
        Assert.Equal(AccountRoles.LibraryManager, exceptionLog.ActorRole);
        Assert.Equal("Thủ thư được ủy quyền", exceptionLog.Actor);
        Assert.NotNull(exceptionLog.BookLoanId);
        Assert.Equal(2, await fixture.Db.BookLoans.CountAsync(loan => loan.ReaderAccountId == fixture.Reader.Id));

        var repeated = await controller.CreateApi(model);
        var conflict = Assert.IsType<ConflictObjectResult>(repeated);
        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal(2, await fixture.Db.BookLoans.CountAsync(loan => loan.ReaderAccountId == fixture.Reader.Id));
        Assert.Single(await fixture.Db.AuditBlockLogs.ToListAsync());

        var nextLoan = await fixture.Service.CreateAsync(
            fixture.Book.Id, fixture.Reader.Id, DateOnly.FromDateTime(DateTime.Today));
        Assert.False(nextLoan.IsSuccess);
        Assert.Contains("Đang mượn 2/1 cuốn", nextLoan.ErrorMessage);
        Assert.Equal(2, await fixture.Db.BookLoans.CountAsync(loan => loan.ReaderAccountId == fixture.Reader.Id));
    }

    [Theory]
    [InlineData(AccountRoles.Librarian)]
    [InlineData(AccountRoles.SystemAdmin)]
    public async Task CreateApi_NonManagerCannotOverrideEvenWithDirectRequest(string role)
    {
        await using var fixture = await Fixture.CreateAsync(limit: 1, borrowedCount: 1);
        var controller = await CreateLoanControllerAsync(fixture, role);
        var model = new CreateBookLoanViewModel
        {
            BookId = fixture.Book.Id,
            ReaderAccountId = fixture.Reader.Id,
            LoanDate = DateOnly.FromDateTime(DateTime.Today),
            OverrideBlocked = true,
            OverrideReason = "Tôi muốn bỏ qua.",
            OverrideRequestId = Guid.NewGuid().ToString()
        };

        var result = await controller.CreateApi(model);

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbidden.StatusCode);
        Assert.Single(await fixture.Db.BookLoans.Where(loan => loan.ReaderAccountId == fixture.Reader.Id).ToListAsync());
        Assert.Empty(await fixture.Db.AuditBlockLogs.ToListAsync());
    }

    [Fact]
    public async Task CreateApi_LibraryManagerCannotOverrideWithoutReason()
    {
        await using var fixture = await Fixture.CreateAsync(limit: 1, borrowedCount: 1);
        var controller = await CreateLoanControllerAsync(fixture, AccountRoles.LibraryManager);
        var model = new CreateBookLoanViewModel
        {
            BookId = fixture.Book.Id,
            ReaderAccountId = fixture.Reader.Id,
            LoanDate = DateOnly.FromDateTime(DateTime.Today),
            OverrideBlocked = true,
            OverrideReason = "   ",
            OverrideRequestId = Guid.NewGuid().ToString()
        };

        var result = await controller.CreateApi(model);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Single(await fixture.Db.BookLoans.Where(loan => loan.ReaderAccountId == fixture.Reader.Id).ToListAsync());
        Assert.Empty(await fixture.Db.AuditBlockLogs.ToListAsync());
    }

    [Fact]
    public async Task CreateApi_FailedOverrideIsLoggedAndCannotBeReplayed()
    {
        await using var fixture = await Fixture.CreateAsync(limit: 1, borrowedCount: 1);
        var requestId = Guid.NewGuid();
        var controller = await CreateLoanControllerAsync(fixture, AccountRoles.LibraryManager);
        var model = new CreateBookLoanViewModel
        {
            BookId = int.MaxValue,
            ReaderAccountId = fixture.Reader.Id,
            LoanDate = DateOnly.FromDateTime(DateTime.Today),
            OverrideBlocked = true,
            OverrideReason = "Yêu cầu mượn đặc biệt được quản lý xem xét.",
            OverrideRequestId = requestId.ToString()
        };

        var failed = await controller.CreateApi(model);

        Assert.IsType<BadRequestObjectResult>(failed);
        var log = await fixture.Db.AuditBlockLogs.SingleAsync();
        Assert.Equal(requestId, log.OverrideRequestId);
        Assert.Equal("Failed", log.Outcome);
        Assert.Equal(fixture.Reader.Id, log.ReaderAccountId);
        Assert.Equal(1, log.BorrowedCount);
        Assert.Equal(1, log.BorrowingLimit);
        Assert.Contains("Đang mượn 1/1 cuốn", log.Reasons);
        Assert.Equal("Không tìm thấy sách.", log.FailureReason);
        Assert.Single(await fixture.Db.BookLoans.Where(loan => loan.ReaderAccountId == fixture.Reader.Id).ToListAsync());
        Assert.Contains(await fixture.Db.AuditLogs.Select(entry => entry.Target).ToListAsync(),
            target => target.Contains("Không tìm thấy sách", StringComparison.Ordinal));

        model.BookId = fixture.Book.Id;
        var replay = await controller.CreateApi(model);

        var conflict = Assert.IsType<ConflictObjectResult>(replay);
        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal(1, await fixture.Db.BookLoans.CountAsync(loan => loan.ReaderAccountId == fixture.Reader.Id));
        Assert.Single(await fixture.Db.AuditBlockLogs.ToListAsync());
    }

    [Fact]
    public async Task GetBorrowingStatus_RefreshesBalanceAfterPaymentIsRecorded()
    {
        await using var fixture = await Fixture.CreateAsync(limit: 5, borrowedCount: 0);
        fixture.Reader.OutstandingBalance = 50000m;
        await fixture.Db.SaveChangesAsync();

        var beforePayment = await fixture.Service.GetBorrowingStatusAsync(fixture.Reader.Id);
        fixture.Reader.OutstandingBalance = 0m;
        await fixture.Db.SaveChangesAsync();
        var afterPayment = await fixture.Service.GetBorrowingStatusAsync(fixture.Reader.Id);

        Assert.Equal(50000m, beforePayment!.OutstandingBalance);
        Assert.Contains(beforePayment.BlockingReasons, reason => reason.Code == "UNPAID_FEE");
        Assert.Equal(0m, afterPayment!.OutstandingBalance);
        Assert.DoesNotContain(afterPayment.BlockingReasons, reason => reason.Code == "UNPAID_FEE");
        Assert.False(afterPayment.IsBlocked);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(50000, "50.000")]
    [InlineData(150000, "150.000")]
    [InlineData(1250000, "1.250.000")]
    [InlineData(125.5, "125,50")]
    public void FormatVnd_PreservesAmountAndUsesVietnameseSeparators(decimal amount, string expected) =>
        Assert.Equal(expected, ReaderBorrowingStatus.FormatVnd(amount));

    [Fact]
    public async Task Create_AtLimit_BlocksLoanAndWritesAuditLogForEveryAttempt()
    {
        await using var fixture = await Fixture.CreateAsync(limit: 5, borrowedCount: 5);

        var first = await fixture.Service.CreateAsync(
            fixture.Book.Id, fixture.Reader.Id, DateOnly.FromDateTime(DateTime.Today),
            actor: "Thủ thư kiểm thử", ipAddress: "127.0.0.1");
        var second = await fixture.Service.CreateAsync(
            fixture.Book.Id, fixture.Reader.Id, DateOnly.FromDateTime(DateTime.Today),
            actor: "Thủ thư kiểm thử", ipAddress: "127.0.0.1");

        Assert.False(first.IsSuccess);
        Assert.Equal("Đang mượn 5/5 cuốn, không thể mượn thêm.", first.ErrorMessage);
        Assert.Equal("BORROWING_LIMIT_REACHED", first.ReasonCode);
        Assert.False(second.IsSuccess);
        Assert.Equal(5, await fixture.Db.BookLoans.CountAsync(loan => loan.ReaderAccountId == fixture.Reader.Id));

        var logs = await fixture.Db.AuditLogs.OrderBy(log => log.Id).ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.All(logs, log =>
        {
            Assert.Equal("Thủ thư kiểm thử", log.Actor);
            Assert.Equal(AuditActions.BlockLoanLimit, log.Action);
            Assert.Contains(fixture.Reader.StudentOrStaffCode, log.Target);
            Assert.Contains(fixture.Reader.FullName, log.Target);
            Assert.Contains("5/5", log.Target);
            Assert.Equal("127.0.0.1", log.IpAddress);
            Assert.NotEqual(default, log.OccurredAtUtc);
        });
    }

    [Fact]
    public async Task Create_OverLimit_AlsoBlocks()
    {
        await using var fixture = await Fixture.CreateAsync(limit: 5, borrowedCount: 6);

        var result = await fixture.Service.CreateAsync(
            fixture.Book.Id, fixture.Reader.Id, DateOnly.FromDateTime(DateTime.Today));

        Assert.False(result.IsSuccess);
        Assert.Equal("Đang mượn 6/5 cuốn, không thể mượn thêm.", result.ErrorMessage);
        Assert.Equal(6, await fixture.Db.BookLoans.CountAsync(loan => loan.ReaderAccountId == fixture.Reader.Id));
        Assert.Single(await fixture.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task GetBorrowingStatus_ExcludesReturnedLoans_AndUsesCardTypeLimit()
    {
        await using var fixture = await Fixture.CreateAsync(
            limit: LibraryCardType.DefaultMaxBorrowedBooks,
            borrowedCount: 5,
            returnedIndexes: [1, 4]);

        var status = await fixture.Service.GetBorrowingStatusAsync(fixture.Reader.Id);

        Assert.Equal(3, status!.BorrowedCount);
        Assert.Equal(100, status.BorrowingLimit);
        Assert.Equal(97, status.Remaining);
        Assert.False(status.IsBlocked);
    }

    [Fact]
    public async Task NewCardTypesDefaultToOneHundredBorrowedBooks()
    {
        var type = new LibraryCardType { Name = "Thẻ mới" };
        Assert.Equal(100, type.MaxBorrowedBooks);
    }

    [Fact]
    public async Task Create_ExpiredCard_BlocksAndWritesReason()
    {
        await using var fixture = await Fixture.CreateAsync(limit: 5, borrowedCount: 0);
        fixture.Reader.LibraryCard!.ExpiresOn = DateOnly.FromDateTime(DateTime.Today).AddDays(-1);
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.CreateAsync(
            fixture.Book.Id, fixture.Reader.Id, DateOnly.FromDateTime(DateTime.Today));
        var status = await fixture.Service.GetBorrowingStatusAsync(fixture.Reader.Id);

        Assert.False(result.IsSuccess);
        Assert.Contains("Thẻ thư viện đã hết hạn.", result.ErrorMessage);
        Assert.Equal(["CARD_EXPIRED"], status!.BlockingReasons.Select(reason => reason.Code));
        Assert.Equal(AuditActions.BlockLoanPolicy, (await fixture.Db.AuditLogs.SingleAsync()).Action);
    }

    [Fact]
    public async Task Create_CardExpiresToday_RemainsValidThroughThatDay()
    {
        await using var fixture = await Fixture.CreateAsync(limit: 5, borrowedCount: 0);
        fixture.Reader.LibraryCard!.ExpiresOn = DateOnly.FromDateTime(DateTime.Today);
        await fixture.Db.SaveChangesAsync();

        var status = await fixture.Service.GetBorrowingStatusAsync(fixture.Reader.Id);
        var result = await fixture.Service.CreateAsync(
            fixture.Book.Id, fixture.Reader.Id, DateOnly.FromDateTime(DateTime.Today));

        Assert.DoesNotContain(status!.BlockingReasons, reason => reason.Code == "CARD_EXPIRED");
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Create_LockedCard_BlocksAndDoesNotChangeCardStatus()
    {
        await using var fixture = await Fixture.CreateAsync(limit: 5, borrowedCount: 0);
        fixture.Reader.LibraryCard!.Status = "Bị khóa";
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.CreateAsync(
            fixture.Book.Id, fixture.Reader.Id, DateOnly.FromDateTime(DateTime.Today));

        Assert.False(result.IsSuccess);
        Assert.Contains("Thẻ thư viện đang bị khóa.", result.ErrorMessage);
        Assert.Equal("Bị khóa", fixture.Reader.LibraryCard.Status);
        Assert.Equal(AuditActions.BlockLoanPolicy, (await fixture.Db.AuditLogs.SingleAsync()).Action);
    }

    [Fact]
    public async Task Create_OverdueUnreturnedLoan_BlocksButReturnedOverdueLoanIsIgnored()
    {
        await using var fixture = await Fixture.CreateAsync(limit: 5, borrowedCount: 2, returnedIndexes: [1]);
        var loans = await fixture.Db.BookLoans.OrderBy(loan => loan.Id).ToListAsync();
        foreach (var loan in loans) loan.DueDate = DateOnly.FromDateTime(DateTime.Today).AddDays(-1);
        await fixture.Db.SaveChangesAsync();

        var status = await fixture.Service.GetBorrowingStatusAsync(fixture.Reader.Id);
        var result = await fixture.Service.CreateAsync(
            fixture.Book.Id, fixture.Reader.Id, DateOnly.FromDateTime(DateTime.Today));

        Assert.Equal(1, status!.OverdueLoanCount);
        Assert.Contains(status.BlockingReasons, reason => reason.Code == "OVERDUE_UNRETURNED_LOANS");
        Assert.False(result.IsSuccess);
        Assert.Contains("Bạn đọc có sách quá hạn chưa trả.", result.ErrorMessage);
    }

    [Fact]
    public async Task Create_MultipleViolations_ReturnsOrderedReasonsAndLogsEveryReason()
    {
        await using var fixture = await Fixture.CreateAsync(limit: 5, borrowedCount: 5);
        fixture.Reader.FullName = new string('N', 100);
        fixture.Reader.StudentOrStaffCode = new string('R', 50);
        fixture.Reader.LibraryCard!.ExpiresOn = DateOnly.FromDateTime(DateTime.Today).AddDays(-1);
        fixture.Reader.LibraryCard.Status = "Bị khóa";
        fixture.Reader.OutstandingBalance = 150000m;
        var loans = await fixture.Db.BookLoans.Where(loan => loan.ReturnedAtUtc == null).ToListAsync();
        foreach (var loan in loans) loan.DueDate = DateOnly.FromDateTime(DateTime.Today).AddDays(-1);
        await fixture.Db.SaveChangesAsync();

        var status = await fixture.Service.GetBorrowingStatusAsync(fixture.Reader.Id);
        var result = await fixture.Service.CreateAsync(
            fixture.Book.Id, fixture.Reader.Id, DateOnly.FromDateTime(DateTime.Today),
            actor: "Thủ thư", ipAddress: "127.0.0.1");

        Assert.Equal(
            ["CARD_EXPIRED", "CARD_LOCKED", "OVERDUE_UNRETURNED_LOANS", "UNPAID_FEE", "BORROWING_LIMIT_REACHED"],
            status!.BlockingReasons.Select(reason => reason.Code));
        Assert.False(result.IsSuccess);
        Assert.Equal("BORROWING_POLICY_VIOLATION", result.ReasonCode);
        Assert.Equal(5, result.BlockingReasons!.Count);
        Assert.Equal(5, await fixture.Db.BookLoans.CountAsync(loan => loan.ReaderAccountId == fixture.Reader.Id));
        var log = await fixture.Db.AuditLogs.SingleAsync();
        Assert.Equal(AuditActions.BlockLoanPolicy, log.Action);
        Assert.Contains("Thẻ thư viện đã hết hạn.", log.Target);
        Assert.Contains("Thẻ thư viện đang bị khóa.", log.Target);
        Assert.Contains("Bạn đọc có sách quá hạn chưa trả.", log.Target);
        Assert.Contains("nợ chưa thanh toán=150.000 VND", log.Target);
        Assert.Contains("Đang mượn 5/5 cuốn", log.Target);
        Assert.True(log.Target.Length <= 500);
        var blockLog = await fixture.Db.AuditBlockLogs.SingleAsync();
        Assert.Equal(150000m, blockLog.OutstandingBalance);
        Assert.Contains("Còn nợ 150.000 VND", blockLog.Reasons);
        Assert.Equal("CARD_EXPIRED|CARD_LOCKED|OVERDUE_UNRETURNED_LOANS|UNPAID_FEE|BORROWING_LIMIT_REACHED",
            blockLog.BlockingReasonCodes);
        Assert.Equal(LoanBlockHistoryEventTypes.BlockedMultiple, blockLog.EventType);
    }

    [Fact]
    public async Task Return_RecordsTimestampAndAudit_AndReleasesBorrowingLimit()
    {
        await using var fixture = await Fixture.CreateAsync(limit: 5, borrowedCount: 5);
        var loan = await fixture.Db.BookLoans.FirstAsync(item => item.ReturnedAtUtc == null);

        var result = await fixture.Service.ReturnAsync(loan.Id, "Thủ thư kiểm thử", "127.0.0.1");
        var status = await fixture.Service.GetBorrowingStatusAsync(fixture.Reader.Id);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Loan!.ReturnedAtUtc);
        Assert.Equal(4, status!.BorrowedCount);
        var log = Assert.Single(await fixture.Db.AuditLogs.ToListAsync());
        Assert.Equal(AuditActions.ReturnBookLoan, log.Action);
        Assert.Equal("Thủ thư kiểm thử", log.Actor);
        Assert.Contains(fixture.Reader.FullName, log.Target);
    }

    [Fact]
    public async Task Return_RejectsDuplicateWithoutWritingSecondAuditLog()
    {
        await using var fixture = await Fixture.CreateAsync(limit: 5, borrowedCount: 1);
        var loan = await fixture.Db.BookLoans.SingleAsync();

        var first = await fixture.Service.ReturnAsync(loan.Id, "Thủ thư", "127.0.0.1");
        var second = await fixture.Service.ReturnAsync(loan.Id, "Thủ thư", "127.0.0.1");

        Assert.True(first.IsSuccess);
        Assert.False(second.IsSuccess);
        Assert.Equal("Phiếu mượn đã được ghi nhận trả sách.", second.ErrorMessage);
        Assert.Single(await fixture.Db.AuditLogs.ToListAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(ApplicationDbContext db, BookLoanService service, ReaderAccount reader, Book book)
        {
            Db = db;
            Service = service;
            Reader = reader;
            Book = book;
        }

        public ApplicationDbContext Db { get; }
        public BookLoanService Service { get; }
        public ReaderAccount Reader { get; }
        public Book Book { get; }

        public static async Task<Fixture> CreateAsync(
            int limit, int borrowedCount, int[]? returnedIndexes = null)
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var db = new ApplicationDbContext(options);
            var cardType = new LibraryCardType
            {
                Name = $"Test-{Guid.NewGuid()}",
                MaxBorrowedBooks = limit
            };
            var reader = new ReaderAccount
            {
                FullName = "Bạn đọc kiểm thử",
                DateOfBirth = new DateOnly(2000, 1, 1),
                Email = $"{Guid.NewGuid()}@example.test",
                PhoneNumber = "0900000000",
                StudentOrStaffCode = $"TEST-{Guid.NewGuid():N}",
                PasswordHash = "hash",
                Status = "Đang hoạt động",
                LibraryCard = new LibraryCard
                {
                    CardCode = Guid.NewGuid().ToString("N"),
                    LibraryCardType = cardType,
                    IssuedOn = DateOnly.FromDateTime(DateTime.Today),
                    ExpiresOn = DateOnly.FromDateTime(DateTime.Today).AddYears(1)
                }
            };
            var book = new Book { Title = "Sách kiểm thử", AuthorId = 1 };
            db.ReaderAccounts.Add(reader);
            db.Books.Add(book);
            await db.SaveChangesAsync();

            for (var i = 0; i < borrowedCount; i++)
            {
                db.BookLoans.Add(new BookLoan
                {
                    BookId = book.Id,
                    ReaderAccountId = reader.Id,
                    LoanDate = DateOnly.FromDateTime(DateTime.Today),
                    OriginalDueDate = DateOnly.FromDateTime(DateTime.Today).AddDays(14),
                    DueDate = DateOnly.FromDateTime(DateTime.Today).AddDays(14),
                    ReturnedAtUtc = returnedIndexes?.Contains(i) == true ? DateTime.UtcNow : null
                });
            }
            await db.SaveChangesAsync();

            var schedules = new WorkingScheduleService(db);
            foreach (var day in Enum.GetValues<DayOfWeek>())
                await schedules.UpdateWeeklyScheduleAsync(day, true, null);
            return new Fixture(db, new BookLoanService(db, schedules), reader, book);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class NoopObjectModelValidator : IObjectModelValidator
    {
        public void Validate(
            ActionContext actionContext,
            ValidationStateDictionary? validationState,
            string prefix,
            object? model)
        {
        }
    }

    private static async Task<LoanController> CreateLoanControllerAsync(Fixture fixture, string role)
    {
        const string refreshToken = "override-test-refresh-token";
        var staff = new AdminAccount
        {
            FullName = "Thủ thư được ủy quyền",
            Email = $"{Guid.NewGuid()}@example.test",
            PasswordHash = "hash",
            Role = role
        };
        fixture.Db.AdminAccounts.Add(staff);
        fixture.Db.RefreshTokens.Add(new RefreshToken
        {
            AdminAccount = staff,
            TokenHash = TokenService.HashRefreshToken(refreshToken),
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        });
        await fixture.Db.SaveChangesAsync();

        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"admin_refresh={refreshToken}";
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        var controller = new LoanController(
            fixture.Service, fixture.Db,
            new AuditLogService(fixture.Db, NullLogger<AuditLogService>.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            ObjectValidator = new NoopObjectModelValidator()
        };
        return controller;
    }
}
