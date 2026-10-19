using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Project.Controllers;
using Project.Data;
using Project.Models;
using Project.Services;
using Xunit;

namespace Project.Tests;

public class LoanPolicySlice5Tests
{
    private static (ApplicationDbContext Db, BookLoanService Service) CreateTestEnvironment()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(options);
        var calendar = new WorkingScheduleService(db);
        var service = new BookLoanService(db, calendar);
        return (db, service);
    }

    private static async Task<(ReaderAccount Reader, List<Book> Books)> SeedReaderAndBooksAsync(
        ApplicationDbContext db,
        int maxBooks = 5,
        int existingLoans = 0,
        decimal outstandingBalance = 0m,
        bool isCardExpired = false,
        bool isCardLocked = false,
        bool hasOverdueLoan = false)
    {
        var cardType = new LibraryCardType
        {
            Name = "Thẻ sinh viên",
            MaxBooks = maxBooks
        };
        db.LibraryCardTypes.Add(cardType);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var reader = new ReaderAccount
        {
            FullName = "Trần Quản Lý Bỏ Qua",
            DateOfBirth = new DateOnly(2000, 1, 1),
            Email = $"reader-{Guid.NewGuid():N}@example.test",
            PhoneNumber = "0912345678",
            StudentOrStaffCode = $"SV-{Guid.NewGuid():N}"[..8],
            PasswordHash = "hash",
            Status = "Đang hoạt động",
            OutstandingBalance = outstandingBalance,
            LibraryCard = new LibraryCard
            {
                CardCode = $"CARD-{Guid.NewGuid():N}"[..10],
                LibraryCardType = cardType,
                IssuedOn = today.AddYears(-1),
                ExpiresOn = isCardExpired ? today.AddDays(-1) : today.AddYears(1),
                Status = isCardLocked ? "Bị khoá" : "Đang hoạt động"
            }
        };
        db.ReaderAccounts.Add(reader);

        var books = new List<Book>();
        for (var i = 0; i < 10; i++)
        {
            var book = new Book
            {
                Title = $"Sách Slice 5 #{i + 1}",
                Author = new Author { Name = $"Tác giả #{i + 1}" }
            };
            db.Books.Add(book);
            books.Add(book);
        }

        if (hasOverdueLoan)
        {
            var overdueBook = new Book { Title = "Sách Quá Hạn", Author = new Author { Name = "TG Quá Hạn" } };
            db.Books.Add(overdueBook);
            db.BookLoans.Add(new BookLoan
            {
                Book = overdueBook,
                ReaderAccount = reader,
                LoanDate = today.AddDays(-30),
                DueDate = today.AddDays(-10),
                OriginalDueDate = today.AddDays(-10),
                CreatedAtUtc = DateTime.UtcNow.AddDays(-30),
                IsReturned = false
            });
        }

        for (var i = 0; i < existingLoans; i++)
        {
            db.BookLoans.Add(new BookLoan
            {
                Book = books[i],
                ReaderAccount = reader,
                LoanDate = today.AddDays(-2),
                DueDate = today.AddDays(12),
                OriginalDueDate = today.AddDays(12),
                CreatedAtUtc = DateTime.UtcNow.AddDays(-2),
                IsReturned = false
            });
        }

        await db.SaveChangesAsync();
        return (reader, books);
    }

    private static LoanController CreateControllerForStaff(ApplicationDbContext db, BookLoanService service, string email, string role)
    {
        var auditMock = new FixedStaffAuditLog(new AuditLogService(db, NullLogger<AuditLogService>.Instance), email, role);
        var services = new ServiceCollection()
            .AddSingleton<IAuditLogService>(auditMock)
            .BuildServiceProvider();

        var account = new AdminAccount
        {
            FullName = "Nhân viên test",
            Email = email,
            Role = role,
            IsActive = true,
            PasswordHash = "hash"
        };
        db.AdminAccounts.Add(account);
        var token = Guid.NewGuid().ToString("N");
        db.RefreshTokens.Add(new RefreshToken
        {
            AdminAccount = account,
            TokenHash = TokenService.HashRefreshToken(token),
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(1)
        });
        db.SaveChanges();

        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");
        context.Request.Headers.Cookie = $"admin_refresh={token}";

        var controller = new LoanController(service, db)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            Url = new FixedUrlHelper()
        };
        controller.TempData = new TempDataDictionary(context, new TestTempDataProvider());
        return controller;
    }

    private sealed class FixedUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();
        public string? Action(UrlActionContext actionContext) => "/" + actionContext.Action;
        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url) => true;
        public string? Link(string? routeName, object? values) => "/";
        public string? RouteUrl(UrlRouteContext routeContext) => "/";
    }

    // ---------- 1. Xác định quyền bỏ qua ----------

    [Fact]
    public void HasOverridePermission_LibraryManagerAndSystemAdminHavePermission_LibrarianDoesNot()
    {
        Assert.True(LoanController.HasOverridePermission(AccountRoles.LibraryManager));
        Assert.True(LoanController.HasOverridePermission(AccountRoles.SystemAdmin));
        Assert.False(LoanController.HasOverridePermission(AccountRoles.Librarian));
        Assert.False(LoanController.HasOverridePermission("UnknownRole"));
        Assert.False(LoanController.HasOverridePermission(null));
    }

    [Fact]
    public async Task LoanIndexViewModel_CanOverride_IsTrueForManagerAndAdmin()
    {
        var (db, service) = CreateTestEnvironment();
        await SeedReaderAndBooksAsync(db);

        // Manager sees CanOverride == true
        var managerController = CreateControllerForStaff(db, service, "manager@thuvien.vn", AccountRoles.LibraryManager);
        var managerView = Assert.IsType<ViewResult>(await managerController.Index());
        var managerModel = Assert.IsType<LoanIndexViewModel>(managerView.Model);
        Assert.True(managerModel.CanOverride);

        // SystemAdmin sees CanOverride == true
        var adminController = CreateControllerForStaff(db, service, "admin@thuvien.vn", AccountRoles.SystemAdmin);
        var adminView = Assert.IsType<ViewResult>(await adminController.Index());
        var adminModel = Assert.IsType<LoanIndexViewModel>(adminView.Model);
        Assert.True(adminModel.CanOverride);

        // Librarian is redirected because LoanController requires Manager or Admin
        var librarianController = CreateControllerForStaff(db, service, "librarian@thuvien.vn", AccountRoles.Librarian);
        var librarianResult = await librarianController.Index();
        Assert.IsType<RedirectToActionResult>(librarianResult);
    }

    // ---------- 2. Bỏ qua với lý do hợp lệ: thành công và tạo phiếu mượn ----------

    [Fact]
    public async Task OverrideCreateAsync_WithValidReason_SucceedsAndCreatesLoan()
    {
        var (db, service) = CreateTestEnvironment();
        var (reader, books) = await SeedReaderAndBooksAsync(db, outstandingBalance: 50000m);
        var today = DateOnly.FromDateTime(DateTime.Today);

        const string bypassReason = "Cho mượn theo quyết định của quản lý thư viện.";
        var outcome = await service.OverrideCreateAsync(books[0].Id, reader.Id, today, "manager@thuvien.vn", bypassReason);

        Assert.True(outcome.IsSuccess);
        Assert.NotNull(outcome.Loan);
        Assert.Equal(books[0].Id, outcome.Loan.BookId);
        Assert.Equal(reader.Id, outcome.Loan.ReaderAccountId);

        // Phiếu mượn được lưu trong cơ sở dữ liệu
        var loansInDb = await db.BookLoans.Where(l => l.ReaderAccountId == reader.Id).ToListAsync();
        Assert.Single(loansInDb);
    }

    [Fact]
    public async Task CardLockCannotBeBypassedAndExpiredTimedLockAllowsLoansAgain()
    {
        var (db, service) = CreateTestEnvironment();
        var (lockedReader, lockedBooks) = await SeedReaderAndBooksAsync(db, isCardLocked: true);
        var today = DateOnly.FromDateTime(DateTime.Today);

        var bypassed = await service.OverrideCreateAsync(
            lockedBooks[0].Id, lockedReader.Id, today, "manager@thuvien.vn", "Cho phép ngoại lệ");

        Assert.False(bypassed.IsSuccess);
        Assert.Contains("khoá", bypassed.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await db.BookLoans.Where(loan => loan.ReaderAccountId == lockedReader.Id).ToListAsync());
        Assert.Empty(await db.AuditLogs.Where(log => log.Action == AuditActions.OverrideBlockLoan).ToListAsync());

        var (expiredReader, expiredBooks) = await SeedReaderAndBooksAsync(db);
        expiredReader.LibraryCard!.Status = "Đang bị khoá";
        expiredReader.LibraryCard.LockReason = "Khoá có thời hạn";
        expiredReader.LibraryCard.LockedAtUtc = DateTime.UtcNow.AddHours(-2);
        expiredReader.LibraryCard.LockExpiresAtUtc = DateTime.UtcNow.AddHours(-1);
        expiredReader.LibraryCard.IsLockUntilManualUnlock = false;
        await db.SaveChangesAsync();

        var afterExpiry = await service.CreateAsync(expiredBooks[0].Id, expiredReader.Id, today);

        Assert.True(afterExpiry.IsSuccess);
        Assert.Single(await db.BookLoans.Where(loan => loan.ReaderAccountId == expiredReader.Id).ToListAsync());
    }

    [Fact]
    public async Task LockedCardCannotRenewAndDoesNotChangeLoan()
    {
        var (db, service) = CreateTestEnvironment();
        var (reader, books) = await SeedReaderAndBooksAsync(db, isCardLocked: true);
        var dueDate = DateOnly.FromDateTime(DateTime.Today).AddDays(5);
        var loan = new BookLoan
        {
            Book = books[0],
            ReaderAccount = reader,
            LoanDate = dueDate.AddDays(-10),
            OriginalDueDate = dueDate,
            DueDate = dueDate,
            RenewalCount = 0
        };
        db.BookLoans.Add(loan);
        await db.SaveChangesAsync();

        var result = await service.RenewAsync(loan.Id, DateOnly.FromDateTime(DateTime.Today));

        Assert.False(result.IsSuccess);
        Assert.Contains("khoá", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        var stored = await db.BookLoans.SingleAsync(item => item.Id == loan.Id);
        Assert.Equal(dueDate, stored.DueDate);
        Assert.Equal(0, stored.RenewalCount);
    }

    // ---------- 3. Bắt buộc nhập lý do: không nhập hoặc chỉ chứa khoảng trắng ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task OverrideCreateAsync_EmptyOrWhitespaceReason_FailsAndDoesNotCreateLoan(string invalidReason)
    {
        var (db, service) = CreateTestEnvironment();
        var (reader, books) = await SeedReaderAndBooksAsync(db, outstandingBalance: 50000m);
        var today = DateOnly.FromDateTime(DateTime.Today);

        var outcome = await service.OverrideCreateAsync(books[0].Id, reader.Id, today, "manager@thuvien.vn", invalidReason);

        Assert.False(outcome.IsSuccess);
        Assert.Equal("Vui lòng nhập lý do bỏ qua.", outcome.ErrorMessage);

        // Không tạo phiếu mượn
        Assert.Empty(await db.BookLoans.Where(l => l.ReaderAccountId == reader.Id).ToListAsync());
    }

    // ---------- 4. Bỏ qua chỉ áp dụng một lần: lần sau vẫn kiểm tra và chặn lại ----------

    [Fact]
    public async Task Override_OnlyAppliesToOneTime_NextLoanStillBlocked()
    {
        var (db, service) = CreateTestEnvironment();
        var (reader, books) = await SeedReaderAndBooksAsync(db, outstandingBalance: 50000m);
        var today = DateOnly.FromDateTime(DateTime.Today);

        // Lần 1: Bị chặn -> Quản lý bỏ qua với lý do -> Tạo phiếu thành công
        var firstOutcome = await service.OverrideCreateAsync(
            books[0].Id, reader.Id, today, "manager@thuvien.vn", "Quyết định đặc cách của quản lý");
        Assert.True(firstOutcome.IsSuccess);

        // Lần 2: Thao tác cho mượn thông thường với cùng bạn đọc
        var secondOutcome = await service.CreateAsync(books[1].Id, reader.Id, today);

        // Phải tiếp tục chặn lại vì bạn đọc vẫn đang nợ phí
        Assert.False(secondOutcome.IsSuccess);
        Assert.Contains("50.000 VND", secondOutcome.ErrorMessage);
    }

    [Fact]
    public async Task Override_DoesNotMutateReaderPolicyState()
    {
        // Kiểm tra việc bỏ qua không thay đổi vĩnh viễn hạn mức, trạng thái thẻ, hay số dư nợ của bạn đọc
        var (db, service) = CreateTestEnvironment();
        var (reader, books) = await SeedReaderAndBooksAsync(db, outstandingBalance: 75000m, isCardExpired: true);
        var today = DateOnly.FromDateTime(DateTime.Today);

        await service.OverrideCreateAsync(books[0].Id, reader.Id, today, "manager@thuvien.vn", "Bỏ qua một lần");

        // Tải lại bạn đọc từ DB
        var reloadedReader = await db.ReaderAccounts.Include(r => r.LibraryCard).SingleAsync(r => r.Id == reader.Id);
        Assert.Equal(75000m, reloadedReader.OutstandingBalance);
        Assert.True(reloadedReader.LibraryCard!.ExpiresOn < today); // Thẻ vẫn hết hạn
    }

    // ---------- 5. Ghi nhật ký hành động bỏ qua đầy đủ ----------

    [Fact]
    public async Task FullAuditTrail_BlockedThenOverriddenThenCreated()
    {
        var (db, service) = CreateTestEnvironment();
        var (reader, books) = await SeedReaderAndBooksAsync(db, outstandingBalance: 50000m);
        var today = DateOnly.FromDateTime(DateTime.Today);

        // 1. Thao tác thông thường bị chặn
        var blockOutcome = await service.CreateAsync(books[0].Id, reader.Id, today, "thuthu@thuvien.vn");
        Assert.False(blockOutcome.IsSuccess);

        // 2. Quản lý thực hiện bỏ qua
        const string bypassReason = "Cho mượn theo quyết định của quản lý thư viện.";
        var overrideOutcome = await service.OverrideCreateAsync(
            books[0].Id, reader.Id, today, "quanly@thuvien.vn", bypassReason);
        Assert.True(overrideOutcome.IsSuccess);

        // Lấy toàn bộ nhật ký chặn / bỏ qua
        var logs = await service.GetBlockedLoanLogsAsync(reader.Id);
        Assert.Equal(2, logs.Count);

        // Bản ghi bỏ qua (mới nhất)
        var overrideLog = logs[0];
        Assert.Equal("Đã bỏ qua", overrideLog.Status);
        Assert.Equal("quanly@thuvien.vn", overrideLog.Operator);
        Assert.Equal(bypassReason, overrideLog.BypassReason);
        Assert.Contains("50.000 VND", overrideLog.Reason);
        Assert.Equal(reader.FullName, overrideLog.ReaderName);

        // Bản ghi chặn ban đầu vẫn tồn tại
        var blockLog = logs[1];
        Assert.Equal("Bị chặn", blockLog.Status);
        Assert.Equal("thuthu@thuvien.vn", blockLog.Operator);
        Assert.Null(blockLog.BypassReason);
        Assert.Contains("50.000 VND", blockLog.Reason);
    }

    // ---------- 6. Kiểm tra quyền ở cấp Controller (MVC & API) ----------

    [Fact]
    public async Task Controller_Override_WithManagerPermission_Succeeds()
    {
        var (db, service) = CreateTestEnvironment();
        var (reader, books) = await SeedReaderAndBooksAsync(db, outstandingBalance: 60000m);
        var controller = CreateControllerForStaff(db, service, "manager@thuvien.vn", AccountRoles.LibraryManager);

        var result = await controller.Override(new OverrideBookLoanViewModel
        {
            BookId = books[0].Id,
            ReaderAccountId = reader.Id,
            LoanDate = DateOnly.FromDateTime(DateTime.Today),
            BypassReason = "Quản lý duyệt trực tiếp."
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Contains("Đã bỏ qua chặn", controller.TempData["SuccessMessage"]?.ToString());

        // Phiếu mượn được tạo
        Assert.Single(await db.BookLoans.Where(l => l.ReaderAccountId == reader.Id).ToListAsync());
    }

    [Fact]
    public async Task Controller_Override_WithoutPermission_IsRejected()
    {
        var (db, service) = CreateTestEnvironment();
        var (reader, books) = await SeedReaderAndBooksAsync(db, outstandingBalance: 60000m);
        // Thủ thư (Librarian) không có quyền bỏ qua
        var controller = CreateControllerForStaff(db, service, "librarian@thuvien.vn", AccountRoles.Librarian);

        var result = await controller.Override(new OverrideBookLoanViewModel
        {
            BookId = books[0].Id,
            ReaderAccountId = reader.Id,
            LoanDate = DateOnly.FromDateTime(DateTime.Today),
            BypassReason = "Thủ thư cố bỏ qua."
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Bạn không có quyền bỏ qua chặn cho mượn.", controller.TempData["ErrorMessage"]);

        // Không có phiếu mượn nào được tạo
        Assert.Empty(await db.BookLoans.Where(l => l.ReaderAccountId == reader.Id).ToListAsync());
    }

    [Fact]
    public async Task Controller_OverrideApi_WithManagerPermission_ReturnsOk()
    {
        var (db, service) = CreateTestEnvironment();
        var (reader, books) = await SeedReaderAndBooksAsync(db, isCardExpired: true);
        var controller = CreateControllerForStaff(db, service, "admin@thuvien.vn", AccountRoles.SystemAdmin);

        var result = await controller.OverrideApi(new OverrideBookLoanViewModel
        {
            BookId = books[0].Id,
            ReaderAccountId = reader.Id,
            LoanDate = DateOnly.FromDateTime(DateTime.Today),
            BypassReason = "Quản trị hệ thống chấp thuận đặc cách."
        });

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(await db.BookLoans.Where(l => l.ReaderAccountId == reader.Id).ToListAsync());
    }

    [Fact]
    public async Task Controller_OverrideApi_WithoutPermission_Returns403Forbidden()
    {
        var (db, service) = CreateTestEnvironment();
        var (reader, books) = await SeedReaderAndBooksAsync(db, isCardExpired: true);
        var controller = CreateControllerForStaff(db, service, "librarian@thuvien.vn", AccountRoles.Librarian);

        var result = await controller.OverrideApi(new OverrideBookLoanViewModel
        {
            BookId = books[0].Id,
            ReaderAccountId = reader.Id,
            LoanDate = DateOnly.FromDateTime(DateTime.Today),
            BypassReason = "Cố gọi API không có quyền."
        });

        var forbid = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbid.StatusCode);
        Assert.Empty(await db.BookLoans.Where(l => l.ReaderAccountId == reader.Id).ToListAsync());
    }

    [Fact]
    public async Task Controller_OverrideApi_EmptyReason_ReturnsBadRequest()
    {
        var (db, service) = CreateTestEnvironment();
        var (reader, books) = await SeedReaderAndBooksAsync(db, outstandingBalance: 30000m);
        var controller = CreateControllerForStaff(db, service, "manager@thuvien.vn", AccountRoles.LibraryManager);

        var result = await controller.OverrideApi(new OverrideBookLoanViewModel
        {
            BookId = books[0].Id,
            ReaderAccountId = reader.Id,
            LoanDate = DateOnly.FromDateTime(DateTime.Today),
            BypassReason = "   "
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Vui lòng nhập lý do bỏ qua.", badRequest.Value?.ToString());
    }

    // ---------- 7. Kiểm tra các lát trước tiếp tục hoạt động đầy đủ ----------

    [Fact]
    public async Task PreviousSlicesConditions_CardLimit_Expired_Locked_Overdue_Debt_AllStillBlock()
    {
        var (db, service) = CreateTestEnvironment();
        var today = DateOnly.FromDateTime(DateTime.Today);

        // Lát 1: Hạn mức
        var (reader1, books1) = await SeedReaderAndBooksAsync(db, maxBooks: 2, existingLoans: 2);
        var res1 = await service.CreateAsync(books1[0].Id, reader1.Id, today);
        Assert.False(res1.IsSuccess);
        Assert.Contains("2/2", res1.ErrorMessage);

        // Lát 2: Thẻ hết hạn
        var (reader2, books2) = await SeedReaderAndBooksAsync(db, isCardExpired: true);
        var res2 = await service.CreateAsync(books2[0].Id, reader2.Id, today);
        Assert.False(res2.IsSuccess);
        Assert.Contains("hết hạn", res2.ErrorMessage);

        // Lát 2: Thẻ bị khoá
        var (reader3, books3) = await SeedReaderAndBooksAsync(db, isCardLocked: true);
        var res3 = await service.CreateAsync(books3[0].Id, reader3.Id, today);
        Assert.False(res3.IsSuccess);
        Assert.Contains("khoá", res3.ErrorMessage);

        // Lát 2: Quá hạn
        var (reader4, books4) = await SeedReaderAndBooksAsync(db, hasOverdueLoan: true);
        var res4 = await service.CreateAsync(books4[0].Id, reader4.Id, today);
        Assert.False(res4.IsSuccess);
        Assert.Contains("quá hạn", res4.ErrorMessage);

        // Lát 3: Nợ phí
        var (reader5, books5) = await SeedReaderAndBooksAsync(db, outstandingBalance: 40000m);
        var res5 = await service.CreateAsync(books5[0].Id, reader5.Id, today);
        Assert.False(res5.IsSuccess);
        Assert.Contains("40.000 VND", res5.ErrorMessage);
    }

    // ---------- 8. Kiểm tra parser nhật ký ----------

    [Fact]
    public void ToBlockedLoanLogEntry_ParsesBothBlockedAndOverriddenLogsCorrectly()
    {
        // 1. Log bị chặn thông thường (Lát 4)
        var blockLog = new AuditLog
        {
            Id = 10,
            OccurredAtUtc = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc),
            Actor = "thuthu@thuvien.vn",
            Action = AuditActions.BlockLoan,
            Target = "Bạn đọc #12 Nguyễn Văn A (a@thuvien.vn) – Lý do: Bạn còn nợ 50.000 VND, không thể mượn sách.",
            IpAddress = "127.0.0.1"
        };
        var blockEntry = BookLoanService.ToBlockedLoanLogEntry(blockLog);
        Assert.Equal("Bị chặn", blockEntry.Status);
        Assert.Equal(12, blockEntry.ReaderAccountId);
        Assert.Equal("Nguyễn Văn A", blockEntry.ReaderName);
        Assert.Equal("a@thuvien.vn", blockEntry.ReaderEmail);
        Assert.Equal("Bạn còn nợ 50.000 VND, không thể mượn sách.", blockEntry.Reason);
        Assert.Null(blockEntry.BypassReason);

        // 2. Log bỏ qua (Lát 5)
        var overrideLog = new AuditLog
        {
            Id = 11,
            OccurredAtUtc = new DateTime(2026, 10, 8, 12, 5, 0, DateTimeKind.Utc),
            Actor = "quanly@thuvien.vn",
            Action = AuditActions.OverrideBlockLoan,
            Target = "Bạn đọc #12 Nguyễn Văn A (a@thuvien.vn) – Lý do chặn: Bạn còn nợ 50.000 VND, không thể mượn sách. – Lý do bỏ qua: Đồng ý cho mượn nghiên cứu.",
            IpAddress = "127.0.0.1"
        };
        var overrideEntry = BookLoanService.ToBlockedLoanLogEntry(overrideLog);
        Assert.Equal("Đã bỏ qua", overrideEntry.Status);
        Assert.Equal(12, overrideEntry.ReaderAccountId);
        Assert.Equal("Nguyễn Văn A", overrideEntry.ReaderName);
        Assert.Equal("a@thuvien.vn", overrideEntry.ReaderEmail);
        Assert.Equal("Bạn còn nợ 50.000 VND, không thể mượn sách.", overrideEntry.Reason);
        Assert.Equal("Đồng ý cho mượn nghiên cứu.", overrideEntry.BypassReason);
    }

    private sealed class FixedStaffAuditLog(AuditLogService inner, string email, string role) : IAuditLogService
    {
        public Task WriteAsync(string actor, string action, string target, string? ipAddress, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(actor, action, target, ipAddress, cancellationToken);
        public Task<IReadOnlyList<AuditLog>> GetRecentAsync(int limit, CancellationToken cancellationToken = default) => inner.GetRecentAsync(limit, cancellationToken);
        public Task<IReadOnlyList<AuditLog>> SearchAsync(AuditLogFilter filter, int limit, CancellationToken cancellationToken = default) => inner.SearchAsync(filter, limit, cancellationToken);
        public Task<IReadOnlyList<string>> GetActorsAsync(CancellationToken cancellationToken = default) => inner.GetActorsAsync(cancellationToken);
        public Task<AdminAccount?> GetSignedInStaffAsync(HttpRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<AdminAccount?>(new AdminAccount { Id = 88, Email = email, Role = role, IsActive = true });
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        private readonly Dictionary<string, object> data = new();
        public IDictionary<string, object> LoadTempData(HttpContext context) => data;
        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
            data.Clear();
            foreach (var kv in values) data[kv.Key] = kv.Value;
        }
    }
}
