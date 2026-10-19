using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Net.Http.Headers;
using Project.Controllers;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

/// <summary>S4-04 lát 4: mở khoá thẻ bạn đọc và nhật ký khoá/mở khoá thẻ.</summary>
public sealed class CardUnlockHistoryTests : IDisposable
{
    private const string ManagerEmail = "manager@library.test";
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;
    private readonly EphemeralDataProtectionProvider dataProtection = new();
    private int sequence;

    public CardUnlockHistoryTests()
    {
        connection.Open();
        db = NewContext();
        db.Database.EnsureCreated();
        db.AdminAccounts.Add(new AdminAccount { Id = 1, Email = ManagerEmail, Role = AccountRoles.LibraryManager, IsActive = true, PasswordHash = "hash" });
        db.SaveChanges();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    // ---------- Mở khoá ----------

    [Fact]
    public async Task ManagerUnlocksLockedCardAndHistoryRecordsEverything()
    {
        var reader = await AddReaderAsync("Nguyễn Văn Mở");
        await LockAsync(reader, "Quá hạn trả sách trên 30 ngày", DateTime.UtcNow.AddDays(5));
        var before = DateTime.UtcNow;

        var result = await CardController(AccountRoles.LibraryManager).SetCardUnlocked(reader.Id, "  Đã trả đủ sách  ");

        Assert.IsType<RedirectToActionResult>(result);
        db.ChangeTracker.Clear();
        var card = await db.LibraryCards.AsNoTracking().SingleAsync();
        Assert.Equal("Đang hoạt động", card.Status);
        Assert.False(LibraryCardStatusHelper.IsCardLocked(card));

        var unlock = await db.LibraryCardLockHistories.AsNoTracking().SingleAsync(entry => entry.EventType == LibraryCardLockEvents.Unlock);
        Assert.Equal(ManagerEmail, unlock.PerformedBy);
        Assert.Equal(1, unlock.PerformedByAdminAccountId);
        Assert.Equal("Quá hạn trả sách trên 30 ngày", unlock.LockReason);
        Assert.Equal("Đã trả đủ sách", unlock.UnlockReason);
        Assert.Equal(card.CardCode, unlock.CardCode);
        Assert.Equal("Nguyễn Văn Mở", unlock.ReaderName);
        Assert.NotNull(unlock.LockedAtUtc);
        Assert.NotNull(unlock.LockExpiresAtUtc);
        Assert.InRange(unlock.OccurredAtUtc, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
        Assert.Contains(await db.AuditLogs.Select(log => log.Action).ToListAsync(), action => action == AuditActions.UnlockCard);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t ")]
    public async Task BlankUnlockReasonIsRejected(string? reason)
    {
        var reader = await AddReaderAsync("Lý do trống");
        await LockAsync(reader, "Vi phạm", null);

        var controller = CardController(AccountRoles.LibraryManager);
        await controller.SetCardUnlocked(reader.Id, reason);

        Assert.Contains("lý do mở khoá", controller.TempData["ErrorMessage"] as string);
        await AssertStillLockedWithoutUnlockAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(AccountRoles.Librarian)]
    public async Task UnauthorizedStaffCannotUnlockOrSeeHistory(string? role)
    {
        var reader = await AddReaderAsync("Không đủ quyền");
        await LockAsync(reader, "Vi phạm", null);

        var unlock = await CardController(role).SetCardUnlocked(reader.Id, "Muốn mở");
        var page = await CardController(role).CardLockHistory(new LibraryCardLockHistoryFilter());
        var api = await CardController(role).CardLockHistoryApi(new LibraryCardLockHistoryFilter());

        if (role is null)
        {
            Assert.IsType<RedirectToActionResult>(unlock);
            Assert.IsType<RedirectToActionResult>(page);
            Assert.IsType<UnauthorizedObjectResult>(api);
        }
        else
        {
            Assert.Equal("AccessDenied", Assert.IsType<ViewResult>(unlock).ViewName);
            Assert.Equal("AccessDenied", Assert.IsType<ViewResult>(page).ViewName);
            Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(api).StatusCode);
        }
        await AssertStillLockedWithoutUnlockAsync();
    }

    [Fact]
    public async Task SystemAdminCanAlsoUnlock()
    {
        var reader = await AddReaderAsync("Quản trị mở");
        await LockAsync(reader, "Vi phạm", null);

        Assert.IsType<RedirectToActionResult>(await CardController(AccountRoles.SystemAdmin).SetCardUnlocked(reader.Id, "Xác minh xong"));

        Assert.Single(await db.LibraryCardLockHistories.Where(entry => entry.EventType == LibraryCardLockEvents.Unlock).ToListAsync());
    }

    [Fact]
    public async Task MissingReaderOrCardCannotBeUnlocked()
    {
        Assert.IsType<NotFoundObjectResult>(await CardController(AccountRoles.LibraryManager).SetCardUnlocked(9999, "Mở"));

        var noCard = await AddReaderAsync("Không thẻ", withCard: false);
        var controller = CardController(AccountRoles.LibraryManager);
        await controller.SetCardUnlocked(noCard.Id, "Mở");
        Assert.Contains("chưa được cấp thẻ", controller.TempData["ErrorMessage"] as string);
        Assert.Empty(await db.LibraryCardLockHistories.ToListAsync());
    }

    [Fact]
    public async Task SecondUnlockOfSameLockIsNotRecorded()
    {
        var reader = await AddReaderAsync("Mở hai lần");
        await LockAsync(reader, "Vi phạm", null);
        var service = new ReaderAccountAdminService(db);

        var first = await service.SetCardUnlockedAsync(reader.Id, "Lần 1", 1, ManagerEmail, "203.0.113.9");
        var second = await service.SetCardUnlockedAsync(reader.Id, "Lần 2", 1, ManagerEmail, "203.0.113.9");

        Assert.True(first.IsSuccess);
        Assert.Equal(ReaderAccountAdminStatus.NoChange, second.Status);
        Assert.Single(await db.LibraryCardLockHistories.Where(entry => entry.EventType == LibraryCardLockEvents.Unlock).ToListAsync());
    }

    [Fact]
    public async Task ExpiredTimedLockIsNotUnlockedAgain()
    {
        var reader = await AddReaderAsync("Hết hạn khoá");
        var card = await db.LibraryCards.SingleAsync();
        card.Status = "Đang bị khoá";
        card.LockReason = "Vi phạm cũ";
        card.LockedAtUtc = DateTime.UtcNow.AddDays(-3);
        card.LockExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();

        var result = await new ReaderAccountAdminService(db).SetCardUnlockedAsync(reader.Id, "Mở", 1, ManagerEmail, null);

        Assert.Equal(ReaderAccountAdminStatus.NoChange, result.Status);
        Assert.Contains("hết hạn khoá", result.ErrorMessage);
        Assert.Empty(await db.LibraryCardLockHistories.ToListAsync());
        db.ChangeTracker.Clear();
        Assert.Equal("Vi phạm cũ", (await db.LibraryCards.AsNoTracking().SingleAsync()).LockReason);
    }

    [Fact]
    public async Task DatabaseErrorWhileLoggingLeavesCardLockedAndNoLog()
    {
        var reader = await AddReaderAsync("Lỗi CSDL");
        await LockAsync(reader, "Vi phạm", null);
        var auditCount = await db.AuditLogs.CountAsync();

        await using (var failing = NewContext(new FailOnUnlockHistoryInterceptor()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new ReaderAccountAdminService(failing).SetCardUnlockedAsync(reader.Id, "Mở", 1, ManagerEmail, null));
        }

        await AssertStillLockedWithoutUnlockAsync();
        Assert.Equal(auditCount, await db.AuditLogs.CountAsync());
    }

    // ---------- Nhật ký ----------

    [Fact]
    public async Task LockUnlockLockAgainKeepsEveryEntryNewestFirstAndPersists()
    {
        var reader = await AddReaderAsync("Khoá lại");
        await LockAsync(reader, "Lần khoá 1", null);
        await Task.Delay(5);
        Assert.True((await new ReaderAccountAdminService(db).SetCardUnlockedAsync(reader.Id, "Mở lần 1", 1, ManagerEmail, null)).IsSuccess);
        await Task.Delay(5);
        await LockAsync(reader, "Lần khoá 2", DateTime.UtcNow.AddDays(2));

        // Ngữ cảnh mới (tương đương tải lại trang / khởi động lại ứng dụng) vẫn đọc được đầy đủ nhật ký.
        await using var reloaded = NewContext();
        var entries = await new ReaderAccountAdminService(reloaded).SearchCardLockHistoryAsync(new LibraryCardLockHistoryFilter { ReaderAccountId = reader.Id }, 100);

        Assert.Equal([LibraryCardLockEvents.Lock, LibraryCardLockEvents.Unlock, LibraryCardLockEvents.Lock], entries.Select(entry => entry.EventType));
        Assert.Equal(["Lần khoá 2", "Lần khoá 1", "Lần khoá 1"], entries.Select(entry => entry.LockReason));
        Assert.Equal("Mở lần 1", entries[1].UnlockReason);
    }

    [Fact]
    public async Task HistoryEntriesAreReadOnly()
    {
        var reader = await AddReaderAsync("Chỉ đọc");
        await LockAsync(reader, "Vi phạm", null);

        var entry = await db.LibraryCardLockHistories.SingleAsync();
        entry.LockReason = "Sửa lén";
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        db.ChangeTracker.Clear();

        db.LibraryCardLockHistories.Remove(await db.LibraryCardLockHistories.SingleAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Equal("Vi phạm", (await db.LibraryCardLockHistories.AsNoTracking().SingleAsync()).LockReason);
    }

    [Fact]
    public async Task HistoryCanBeSearchedAndFilteredPerReader()
    {
        var an = await AddReaderAsync("Trần An");
        var binh = await AddReaderAsync("Lê Bình");
        db.LibraryCardLockHistories.AddRange(
            History(an, LibraryCardLockEvents.Lock, new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc)),
            History(an, LibraryCardLockEvents.Unlock, new DateTime(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc)),
            History(binh, LibraryCardLockEvents.Lock, new DateTime(2026, 10, 5, 3, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        var service = new ReaderAccountAdminService(db);

        Assert.Equal(2, (await service.SearchCardLockHistoryAsync(new() { Q = "Trần" }, 100)).Count);
        Assert.Single(await service.SearchCardLockHistoryAsync(new() { Q = binhCard(binh) }, 100));
        Assert.Equal(2, (await service.SearchCardLockHistoryAsync(new() { EventType = LibraryCardLockEvents.Lock }, 100)).Count);
        Assert.Single(await service.SearchCardLockHistoryAsync(new() { EventType = LibraryCardLockEvents.Unlock }, 100));
        Assert.Equal(2, (await service.SearchCardLockHistoryAsync(new() { FromDate = new DateOnly(2026, 9, 10) }, 100)).Count);
        Assert.Equal(2, (await service.SearchCardLockHistoryAsync(new() { ToDate = new DateOnly(2026, 9, 10) }, 100)).Count);
        Assert.Single(await service.SearchCardLockHistoryAsync(new() { FromDate = new DateOnly(2026, 9, 2), ToDate = new DateOnly(2026, 9, 30) }, 100));
        Assert.Equal(2, (await service.SearchCardLockHistoryAsync(new() { ReaderAccountId = an.Id }, 100)).Count);
        Assert.Empty(await service.SearchCardLockHistoryAsync(new() { Q = "không tồn tại" }, 100));

        var page = Assert.IsType<ViewResult>(await CardController(AccountRoles.LibraryManager)
            .CardLockHistory(new LibraryCardLockHistoryFilter { FromDate = new DateOnly(2026, 10, 1), ToDate = new DateOnly(2026, 9, 1) }));
        Assert.NotNull(Assert.IsType<LibraryCardLockHistoryPageViewModel>(page.Model).ErrorMessage);
        Assert.IsType<BadRequestObjectResult>(await CardController(AccountRoles.LibraryManager)
            .CardLockHistoryApi(new LibraryCardLockHistoryFilter { EventType = "Xoá thẻ" }));

        var details = Assert.IsType<ViewResult>(await CardController(AccountRoles.LibraryManager).Details(an.Id));
        Assert.All(Assert.IsType<ReaderAccountAdminDetailsViewModel>(details.Model).CardLockHistory, entry => Assert.Equal(an.Id, entry.ReaderAccountId));

        static string binhCard(ReaderAccount reader) => reader.LibraryCard!.CardCode;
    }

    [Fact]
    public async Task HistoryDetailsAreAvailableToManagers()
    {
        var reader = await AddReaderAsync("Chi tiết");
        await LockAsync(reader, "Vi phạm", null);
        var id = (await db.LibraryCardLockHistories.SingleAsync()).Id;

        var view = Assert.IsType<ViewResult>(await CardController(AccountRoles.LibraryManager).CardLockHistoryDetails(id));
        Assert.Equal("Vi phạm", Assert.IsType<LibraryCardLockHistory>(view.Model).LockReason);
        Assert.IsType<OkObjectResult>(await CardController(AccountRoles.LibraryManager).CardLockHistoryEntryApi(id));
        Assert.IsType<NotFoundObjectResult>(await CardController(AccountRoles.LibraryManager).CardLockHistoryDetails(id + 100));
    }

    // ---------- Đồng bộ với trang cá nhân và giao dịch ----------

    [Fact]
    public async Task UnlockedReaderSeesActiveProfileAndCanRenewAgain()
    {
        var reader = await AddReaderAsync("Gia hạn lại");
        var loan = await AddLoanAsync(reader);
        await LockAsync(reader, "Vi phạm", null);
        var loans = new BookLoanService(db, new WorkingScheduleService(db));
        Assert.False((await loans.RenewAsync(loan.Id, DateOnly.FromDateTime(DateTime.Today))).IsSuccess);

        Assert.True((await new ReaderAccountAdminService(db).SetCardUnlockedAsync(reader.Id, "Đã xử lý", 1, ManagerEmail, null)).IsSuccess);
        db.ChangeTracker.Clear();

        var profile = Assert.IsType<ReaderProfileViewModel>(Assert.IsType<ViewResult>(
            await ReaderController(SessionCookie(reader)).Profile((int?)null)).Model);
        Assert.Equal("Đang hoạt động", profile.CardStatus);
        db.ChangeTracker.Clear();
        var renewed = await loans.RenewAsync(loan.Id, DateOnly.FromDateTime(DateTime.Today));
        Assert.True(renewed.IsSuccess, renewed.ErrorMessage);
    }

    [Fact]
    public async Task UnlockDoesNotLiftOtherBlocksSuchAsDebt()
    {
        var reader = await AddReaderAsync("Còn nợ");
        var loan = await AddLoanAsync(reader);
        await LockAsync(reader, "Nợ phí vượt ngưỡng", null);
        var stored = await db.ReaderAccounts.SingleAsync(item => item.Id == reader.Id);
        stored.OutstandingBalance = 50000m;
        await db.SaveChangesAsync();

        Assert.True((await new ReaderAccountAdminService(db).SetCardUnlockedAsync(reader.Id, "Đã xem xét", 1, ManagerEmail, null)).IsSuccess);
        db.ChangeTracker.Clear();

        var renewed = await new BookLoanService(db, new WorkingScheduleService(db)).RenewAsync(loan.Id, DateOnly.FromDateTime(DateTime.Today));
        Assert.False(renewed.IsSuccess);
        Assert.DoesNotContain("đang bị khoá", renewed.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(50000m, (await db.ReaderAccounts.AsNoTracking().SingleAsync(item => item.Id == reader.Id)).OutstandingBalance);
    }

    // ---------- Hỗ trợ ----------

    private async Task LockAsync(ReaderAccount reader, string reason, DateTime? expiresUtc)
    {
        var result = await new ReaderAccountAdminService(db).SetCardLockedAsync(reader.Id, reason, expiresUtc, expiresUtc is null, 1);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        db.ChangeTracker.Clear();
    }

    private async Task AssertStillLockedWithoutUnlockAsync()
    {
        db.ChangeTracker.Clear();
        var card = await db.LibraryCards.AsNoTracking().SingleAsync();
        Assert.True(LibraryCardStatusHelper.IsCardLocked(card));
        Assert.Equal("Đang bị khoá", card.Status);
        Assert.Empty(await db.LibraryCardLockHistories.Where(entry => entry.EventType == LibraryCardLockEvents.Unlock).ToListAsync());
    }

    private static LibraryCardLockHistory History(ReaderAccount reader, string eventType, DateTime occurredAtUtc) => new()
    {
        LibraryCardId = reader.LibraryCard!.Id, CardCode = reader.LibraryCard.CardCode, ReaderAccountId = reader.Id,
        ReaderName = reader.FullName, EventType = eventType, OccurredAtUtc = occurredAtUtc, PerformedBy = ManagerEmail,
        LockReason = "Vi phạm", UnlockReason = eventType == LibraryCardLockEvents.Unlock ? "Đã xử lý" : null
    };

    private async Task<ReaderAccount> AddReaderAsync(string name, bool withCard = true)
    {
        var index = ++sequence;
        var reader = new ReaderAccount
        {
            FullName = name, DateOfBirth = new DateOnly(2000, 1, 1), Email = $"unlock{index}@example.test",
            PhoneNumber = $"09220000{index:00}", StudentOrStaffCode = $"BD-U-{index}", Address = "Hà Nội",
            Status = "Đang hoạt động", EmailConfirmed = true
        };
        reader.PasswordHash = new PasswordHasher<ReaderAccount>().HashPassword(reader, "password123");
        if (withCard)
        {
            reader.LibraryCard = new LibraryCard
            {
                CardCode = $"CARD-U-{index}",
                LibraryCardType = new LibraryCardType { Name = $"Thẻ mở khoá {index}", MaxRenewals = 2 },
                IssuedOn = DateOnly.FromDateTime(DateTime.Today.AddYears(-1)),
                ExpiresOn = DateOnly.FromDateTime(DateTime.Today.AddYears(1)),
                Status = "Đang hoạt động"
            };
        }
        db.ReaderAccounts.Add(reader);
        await db.SaveChangesAsync();
        return reader;
    }

    private async Task<BookLoan> AddLoanAsync(ReaderAccount reader)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var loan = new BookLoan
        {
            Book = new Book { Title = $"Sách của {reader.FullName}", Author = new Author { Name = $"Tác giả {++sequence}" } },
            ReaderAccountId = reader.Id, LoanDate = today.AddDays(-3), OriginalDueDate = today.AddDays(5), DueDate = today.AddDays(5)
        };
        db.BookLoans.Add(loan);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return loan;
    }

    private ApplicationDbContext NewContext(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).AddInterceptors(interceptors).Options);

    private ReaderAccountController CardController(string? role)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        var emailSender = new NoEmailSender();
        var registration = new ReaderRegistrationService(db, new PasswordHasher<ReaderAccount>(), NullLogger<ReaderRegistrationService>.Instance);
        var controller = new ReaderAccountController(
            new ReaderAccountAdminService(db),
            new ReaderEmailVerificationService(db, emailSender, registration, NullLogger<ReaderEmailVerificationService>.Instance),
            new ReaderPasswordResetService(db, new PasswordHasher<ReaderAccount>(), emailSender, NullLogger<ReaderPasswordResetService>.Instance),
            new FixedStaffAuditLogService(new AuditLogService(db, NullLogger<AuditLogService>.Instance), role),
            new TestEnvironment())
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            Url = new FixedUrlHelper()
        };
        controller.TempData = new TempDataDictionary(context, new NoTempDataProvider());
        return controller;
    }

    private ReaderRegistrationController ReaderController(string? cookie)
    {
        var registration = new ReaderRegistrationService(db, new PasswordHasher<ReaderAccount>(), NullLogger<ReaderRegistrationService>.Instance);
        var controller = new ReaderRegistrationController(registration, new ReaderRegistrationIpRateLimiter(),
            new ReaderPasswordResetService(db, new PasswordHasher<ReaderAccount>(), new NoEmailSender(), NullLogger<ReaderPasswordResetService>.Instance),
            dataProtection, new AuditLogService(db, NullLogger<AuditLogService>.Instance), new NoOpEmailVerificationService());
        var context = new DefaultHttpContext();
        if (cookie is not null) context.Request.Headers.Cookie = cookie;
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, new NoTempDataProvider());
        return controller;
    }

    private string SessionCookie(ReaderAccount reader)
    {
        var context = new DefaultHttpContext();
        ReaderSessionCookies.Append(context, dataProtection, reader);
        return string.Join("; ", SetCookieHeaderValue.ParseList(context.Response.Headers.SetCookie.Select(value => value!).ToList())
            .Select(cookie => $"{cookie.Name}={cookie.Value}"));
    }

    /// <summary>Giả lập lỗi cơ sở dữ liệu đúng lúc ghi nhật ký mở khoá.</summary>
    private sealed class FailOnUnlockHistoryInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<LibraryCardLockHistory>()
                .Any(entry => entry.State == EntityState.Added && entry.Entity.EventType == LibraryCardLockEvents.Unlock))
                throw new InvalidOperationException("Mất kết nối cơ sở dữ liệu (giả lập).");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>Dùng nhật ký thật nhưng giả lập nhân sự đang đăng nhập với vai trò cho trước.</summary>
    private sealed class FixedStaffAuditLogService(AuditLogService inner, string? role) : IAuditLogService
    {
        public Task WriteAsync(string actor, string action, string target, string? ipAddress, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(actor, action, target, ipAddress, cancellationToken);
        public Task<IReadOnlyList<AuditLog>> GetRecentAsync(int limit, CancellationToken cancellationToken = default) => inner.GetRecentAsync(limit, cancellationToken);
        public Task<IReadOnlyList<AuditLog>> SearchAsync(AuditLogFilter filter, int limit, CancellationToken cancellationToken = default) => inner.SearchAsync(filter, limit, cancellationToken);
        public Task<IReadOnlyList<string>> GetActorsAsync(CancellationToken cancellationToken = default) => inner.GetActorsAsync(cancellationToken);
        public Task<AdminAccount?> GetSignedInStaffAsync(HttpRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(role is null ? null : new AdminAccount { Id = 1, Email = ManagerEmail, Role = role, IsActive = true });
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ApplicationName { get; set; } = "Project";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Production";
    }

    private sealed class NoEmailSender : IEmailSender
    {
        public Task SendAsync(string recipient, string subject, string htmlBody, CancellationToken cancellationToken = default) => Task.CompletedTask;
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

    private sealed class NoTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
