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
using Microsoft.Extensions.Logging.Abstractions;
using Project.Controllers;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class ReaderAccountAdminTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;
    private readonly ReaderAccountAdminService service;
    private readonly ReaderRegistrationService registration;

    public ReaderAccountAdminTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        service = new ReaderAccountAdminService(db);
        registration = new ReaderRegistrationService(db, new PasswordHasher<ReaderAccount>(), NullLogger<ReaderRegistrationService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    // ---------- Tra cứu ----------

    [Theory]
    [InlineData("Trần Thị")]
    [InlineData("tran@example.com")]
    [InlineData("0988")]
    [InlineData("SV-777")]
    [InlineData("LIB000077")]
    public async Task SearchFindsReaderByNameEmailPhoneCodeOrCard(string keyword)
    {
        var target = await AddReaderAsync("tran@example.com", "Trần Thị B", "0988777666", "SV-777");
        await AddReaderAsync("other@example.com", "Lê Văn C", "0911000111", "SV-111");
        db.LibraryCards.Add(new LibraryCard
        {
            ReaderAccountId = target.Id, CardCode = "LIB000077",
            LibraryCardType = new LibraryCardType { Name = "Thẻ sinh viên" },
            IssuedOn = new DateOnly(2026, 1, 1), ExpiresOn = new DateOnly(2027, 1, 1), Status = "Đang hoạt động"
        });
        await db.SaveChangesAsync();

        var results = await service.SearchAsync(keyword, null, 200);

        Assert.Equal(target.Id, Assert.Single(results).Id);
    }

    [Fact]
    public async Task SpecialFiltersSelectLockedUnconfirmedAndPendingEmailReaders()
    {
        var locked = await AddReaderAsync("locked@example.com", code: "R-L");
        locked.IsLocked = true;
        var unconfirmed = await AddReaderAsync("unconfirmed@example.com", code: "R-U");
        unconfirmed.EmailConfirmed = false;
        var pending = await AddReaderAsync("pending@example.com", code: "R-P");
        pending.PendingEmail = "new@example.com";
        await AddReaderAsync("normal@example.com", code: "R-N");
        await db.SaveChangesAsync();

        Assert.Equal(locked.Id, Assert.Single(await service.SearchAsync(null, ReaderAccountFilters.Locked, 200)).Id);
        Assert.Equal(unconfirmed.Id, Assert.Single(await service.SearchAsync(null, ReaderAccountFilters.UnconfirmedEmail, 200)).Id);
        Assert.Equal(pending.Id, Assert.Single(await service.SearchAsync(null, ReaderAccountFilters.PendingEmailChange, 200)).Id);
        Assert.Equal(4, (await service.SearchAsync(null, null, 200)).Count);
    }

    // ---------- Sửa thông tin ----------

    [Fact]
    public async Task AdminEditUpdatesFieldsAndListsChanges()
    {
        var reader = await AddReaderAsync("a@example.com", code: "R-1");
        var form = ReaderAccountAdminService.ToForm(reader);
        form.FullName = "Tên Mới";
        form.PhoneNumber = "0999888777";

        var result = await service.UpdateAsync(reader.Id, form);

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Changes!, change => change.StartsWith("họ tên:"));
        Assert.Contains(result.Changes!, change => change.StartsWith("số điện thoại:"));
        Assert.Equal(2, result.Changes!.Count);
        db.ChangeTracker.Clear();
        var stored = await db.ReaderAccounts.SingleAsync();
        Assert.Equal("Tên Mới", stored.FullName);
        Assert.Equal("0999888777", stored.PhoneNumber);
    }

    [Fact]
    public async Task AdminEmailChangeTakesEffectImmediatelyAndClearsPendingRequest()
    {
        var reader = await AddReaderAsync("old@example.com", code: "R-1");
        reader.EmailConfirmed = false;
        reader.PendingEmail = "typo@example.com";
        await db.SaveChangesAsync();
        var form = ReaderAccountAdminService.ToForm(reader);
        form.Email = "correct@example.com";

        Assert.True((await service.UpdateAsync(reader.Id, form)).IsSuccess);

        db.ChangeTracker.Clear();
        var stored = await db.ReaderAccounts.SingleAsync();
        Assert.Equal("correct@example.com", stored.Email);
        Assert.True(stored.EmailConfirmed);
        Assert.Null(stored.PendingEmail);
    }

    [Fact]
    public async Task AdminEditRejectsEmailOrCodeOfAnotherReader()
    {
        var reader = await AddReaderAsync("a@example.com", code: "R-1");
        await AddReaderAsync("b@example.com", code: "R-2");

        var form = ReaderAccountAdminService.ToForm(reader);
        form.Email = "B@example.com";
        Assert.Equal(ReaderAccountAdminStatus.DuplicateEmail, (await service.UpdateAsync(reader.Id, form)).Status);

        form = ReaderAccountAdminService.ToForm(reader);
        form.StudentOrStaffCode = "r-2";
        Assert.Equal(ReaderAccountAdminStatus.DuplicateCode, (await service.UpdateAsync(reader.Id, form)).Status);

        db.ChangeTracker.Clear();
        Assert.Equal("a@example.com", (await db.ReaderAccounts.SingleAsync(item => item.Id == reader.Id)).Email);
    }

    [Fact]
    public async Task SavingWithoutChangesReportsNoChange()
    {
        var reader = await AddReaderAsync("a@example.com", code: "R-1");

        var result = await service.UpdateAsync(reader.Id, ReaderAccountAdminService.ToForm(reader));

        Assert.Equal(ReaderAccountAdminStatus.NoChange, result.Status);
    }

    // ---------- Khoá / mở khoá / đăng xuất ----------

    [Fact]
    public async Task LockingRequiresReasonRevokesSessionsAndBlocksLogin()
    {
        var reader = await AddReaderAsync("a@example.com", code: "R-1");
        var versionBefore = reader.SessionVersion;

        Assert.Equal(ReaderAccountAdminStatus.MissingReason, (await service.SetLockedAsync(reader.Id, true, "  ")).Status);
        var result = await service.SetLockedAsync(reader.Id, true, " Nghi lộ mật khẩu ");

        Assert.True(result.IsSuccess);
        db.ChangeTracker.Clear();
        var stored = await db.ReaderAccounts.SingleAsync();
        Assert.True(stored.IsLocked);
        Assert.Equal("Nghi lộ mật khẩu", stored.LockReason);
        Assert.Equal(versionBefore + 1, stored.SessionVersion);

        var controller = ReaderController();
        var login = await controller.Login("a@example.com", "Password123");
        Assert.IsType<ViewResult>(login);
        Assert.Contains("đang bị khoá", controller.ModelState[string.Empty]!.Errors.Single().ErrorMessage);
    }

    [Fact]
    public async Task UnlockingAllowsLoginAgain()
    {
        var reader = await AddReaderAsync("a@example.com", code: "R-1");
        await service.SetLockedAsync(reader.Id, true, "Tạm khoá");

        Assert.True((await service.SetLockedAsync(reader.Id, false, null)).IsSuccess);

        db.ChangeTracker.Clear();
        var stored = await db.ReaderAccounts.SingleAsync();
        Assert.False(stored.IsLocked);
        Assert.Null(stored.LockReason);
        Assert.IsType<RedirectToActionResult>(await ReaderController().Login("a@example.com", "Password123"));
    }

    [Fact]
    public async Task LockedReaderExistingSessionIsRejected()
    {
        var reader = await AddReaderAsync("a@example.com", code: "R-1");
        var context = new DefaultHttpContext();
        var protector = new EphemeralDataProtectionProvider();
        ReaderSessionCookies.Append(context, protector, reader);
        var cookies = context.Response.Headers.SetCookie.Select(header => header!.Split(';')[0]).ToList();

        await service.SetLockedAsync(reader.Id, true, "Khoá");
        db.ChangeTracker.Clear();

        var request = new DefaultHttpContext();
        request.Request.Headers.Cookie = string.Join("; ", cookies);
        var id = await ReaderSessionCookies.GetReaderIdAsync(request, protector, registration.GetReaderByIdAsync, CancellationToken.None);
        Assert.True(id <= 0);
    }

    [Fact]
    public async Task RevokeSessionsBumpsSessionVersion()
    {
        var reader = await AddReaderAsync("a@example.com", code: "R-1");
        var before = reader.SessionVersion;

        Assert.True((await service.RevokeSessionsAsync(reader.Id)).IsSuccess);

        db.ChangeTracker.Clear();
        Assert.Equal(before + 1, (await db.ReaderAccounts.SingleAsync()).SessionVersion);
    }

    // ---------- Chi tiết + lịch sử ----------

    [Fact]
    public async Task DetailsShowOnlyThisReadersHistory()
    {
        var reader = await AddReaderAsync("a@example.com", code: "R-1");
        var other = await AddReaderAsync("b@example.com", code: "R-2");
        db.AuditLogs.AddRange(
            AuditLogService.Create("a@example.com", AuditActions.Login, $"Tài khoản bạn đọc #{reader.Id} (a@example.com)", "1.1.1.1"),
            AuditLogService.Create("admin@x.com", AuditActions.IssueCard, $"Thẻ LIB1 – bạn đọc #{reader.Id} (tự động)", "1.1.1.1"),
            AuditLogService.Create("b@example.com", AuditActions.Login, $"Tài khoản bạn đọc #{other.Id} (b@example.com)", "1.1.1.1"),
            AuditLogService.Create("x@example.com", AuditActions.Login, $"Tài khoản bạn đọc #{reader.Id}0 (x@example.com)", "1.1.1.1"));
        await db.SaveChangesAsync();

        var details = await service.GetDetailsAsync(reader.Id);

        Assert.Equal(2, details!.History.Count);
        Assert.All(details.History, log => Assert.Contains($"bạn đọc #{reader.Id} ", log.Target));
    }

    // ---------- Phân quyền ----------

    [Theory]
    [InlineData(null)]
    [InlineData(AccountRoles.Librarian)]
    public async Task OnlySystemAdminAndLibraryManagerCanOpenReaderAccounts(string? role)
    {
        var controller = AdminController(role);

        var result = await controller.Index(null, null);

        if (role is null) Assert.IsType<RedirectToActionResult>(result);
        else
        {
            Assert.Equal("AccessDenied", Assert.IsType<ViewResult>(result).ViewName);
            Assert.Equal(StatusCodes.Status403Forbidden, controller.Response.StatusCode);
        }
    }

    [Fact]
    public async Task CardUnlockingResetsCardStateAndWritesAuditLog()
    {
        var reader = await AddReaderAsync("card@example.com", code: "R-CARD");
        db.LibraryCards.Add(new LibraryCard
        {
            ReaderAccountId = reader.Id,
            CardCode = "CARD-001",
            LibraryCardType = new LibraryCardType { Name = "Thẻ sinh viên" },
            IssuedOn = new DateOnly(2026, 1, 1),
            ExpiresOn = new DateOnly(2027, 1, 1),
            Status = "Đang hoạt động",
            LockReason = "Vi phạm",
            LockedAtUtc = DateTime.UtcNow,
            LockExpiresAtUtc = DateTime.UtcNow.AddDays(7),
            IsLockUntilManualUnlock = false
        });
        await db.SaveChangesAsync();

        var result = await service.SetCardUnlockedAsync(reader.Id, "Xem xét xong", 1, "admin@library.test", "203.0.113.5");

        Assert.True(result.IsSuccess);
        db.ChangeTracker.Clear();
        var card = await db.LibraryCards.SingleAsync();
        Assert.Equal("Đang hoạt động", card.Status);
        Assert.Null(card.LockReason);
        Assert.Null(card.LockedAtUtc);
        Assert.Null(card.LockExpiresAtUtc);
        Assert.False(card.IsLockUntilManualUnlock);
        Assert.Contains(await db.AuditLogs.AsNoTracking().Select(log => log.Action).ToListAsync(), action => action == AuditActions.UnlockCard);
    }

    [Theory]
    [InlineData(AccountRoles.LibraryManager)]
    [InlineData(AccountRoles.SystemAdmin)]
    public async Task LibraryManagerAndSystemAdminCanLockCardManuallyAndTemporarily(string role)
    {
        var reader = await AddReaderAsync($"lock-{role}@example.com", code: $"R-{role}");
        var card = new LibraryCard
        {
            ReaderAccountId = reader.Id,
            CardCode = $"CARD-{role}",
            LibraryCardType = new LibraryCardType { Name = "Thẻ sinh viên" },
            IssuedOn = new DateOnly(2026, 1, 1),
            ExpiresOn = new DateOnly(2027, 1, 1),
            Status = "Đang hoạt động"
        };
        db.LibraryCards.Add(card);
        await db.SaveChangesAsync();

        var controller = AdminController(role);

        // 1. Khoá thẻ đến khi mở thủ công
        var lockResult = await controller.SetCardLocked(reader.Id, "Ôm sách quá hạn", "manual", null);
        Assert.IsType<RedirectToActionResult>(lockResult);

        db.ChangeTracker.Clear();
        var updatedCard = await db.LibraryCards.SingleAsync(c => c.ReaderAccountId == reader.Id);
        Assert.Equal("Đang bị khoá", updatedCard.Status);
        Assert.Equal("Ôm sách quá hạn", updatedCard.LockReason);
        Assert.True(updatedCard.IsLockUntilManualUnlock);
        Assert.Null(updatedCard.LockExpiresAtUtc);

        // 2. Mở khoá thẻ
        var unlockResult = await controller.SetCardUnlocked(reader.Id, "Đã nộp phạt đầy đủ");
        Assert.IsType<RedirectToActionResult>(unlockResult);

        db.ChangeTracker.Clear();
        updatedCard = await db.LibraryCards.SingleAsync(c => c.ReaderAccountId == reader.Id);
        Assert.Equal("Đang hoạt động", updatedCard.Status);
        Assert.Null(updatedCard.LockReason);
        Assert.False(updatedCard.IsLockUntilManualUnlock);

        // Kiểm tra nhật ký ghi nhận lý do mở và lý do khóa trước đó
        var unlockLog = await db.AuditLogs.AsNoTracking().FirstOrDefaultAsync(l => l.Action == AuditActions.UnlockCard && l.Target.Contains(card.CardCode));
        Assert.NotNull(unlockLog);
        Assert.Contains("lý do khóa trước đó: Ôm sách quá hạn", unlockLog.Target);
        Assert.Contains("lý do mở khóa: Đã nộp phạt đầy đủ", unlockLog.Target);
    }

    [Fact]
    public async Task CardLockedFilterSelectsOnlyReadersWithLockedCards()
    {
        var cardType = new LibraryCardType { Name = "Thẻ kiểm thử" };
        var activeReader = await AddReaderAsync("active-card@example.com", code: "R-AC");
        db.LibraryCards.Add(new LibraryCard
        {
            ReaderAccountId = activeReader.Id, CardCode = "CARD-AC",
            LibraryCardType = cardType,
            IssuedOn = new DateOnly(2026, 1, 1), ExpiresOn = new DateOnly(2027, 1, 1),
            Status = "Đang hoạt động"
        });

        var lockedReader = await AddReaderAsync("locked-card@example.com", code: "R-LC");
        db.LibraryCards.Add(new LibraryCard
        {
            ReaderAccountId = lockedReader.Id, CardCode = "CARD-LC",
            LibraryCardType = cardType,
            IssuedOn = new DateOnly(2026, 1, 1), ExpiresOn = new DateOnly(2027, 1, 1),
            Status = "Đang bị khoá",
            LockReason = "Vi phạm nợ phí",
            LockedAtUtc = DateTime.UtcNow,
            IsLockUntilManualUnlock = true
        });

        await db.SaveChangesAsync();

        var lockedResults = await service.SearchAsync(null, ReaderAccountFilters.CardLocked, 200);
        Assert.Single(lockedResults);
        Assert.Equal(lockedReader.Id, lockedResults[0].Id);
    }

    [Fact]
    public async Task SystemAdminActionsAreRecordedInActivityLog()
    {
        var reader = await AddReaderAsync("a@example.com", code: "R-1");
        var controller = AdminController(AccountRoles.SystemAdmin);
        var form = ReaderAccountAdminService.ToForm(reader);
        form.PhoneNumber = "0999888777";

        Assert.IsType<RedirectToActionResult>(await controller.Edit(reader.Id, form));
        Assert.IsType<RedirectToActionResult>(await controller.SetLocked(reader.Id, true, "Bạn đọc yêu cầu"));

        var logs = await db.AuditLogs.AsNoTracking().OrderBy(log => log.Id).Select(log => log.Target).ToListAsync();
        Assert.Contains(logs, target => target.Contains("quản trị sửa số điện thoại"));
        Assert.Contains(logs, target => target.Contains("khoá tài khoản, lý do: Bạn đọc yêu cầu"));
    }

    [Fact]
    public async Task AdminConfirmEmailActivatesPendingReaderAndIssuesCard()
    {
        db.LibraryCardTypes.Add(new LibraryCardType { Name = "Thẻ bạn đọc thường" });
        await db.SaveChangesAsync();
        var reader = await AddReaderAsync("a@example.com", code: "R-1");
        reader.EmailConfirmed = false;
        reader.Status = "Chờ duyệt";
        await db.SaveChangesAsync();

        Assert.IsType<RedirectToActionResult>(await AdminController(AccountRoles.SystemAdmin).ConfirmEmail(reader.Id));

        db.ChangeTracker.Clear();
        var stored = await db.ReaderAccounts.Include(item => item.LibraryCard).SingleAsync();
        Assert.True(stored.EmailConfirmed);
        Assert.Equal("Đang hoạt động", stored.Status);
        Assert.NotNull(stored.LibraryCard);
        Assert.Contains(await db.AuditLogs.Select(log => log.Action).ToListAsync(), action => action == AuditActions.IssueCard);
    }

    private async Task<ReaderAccount> AddReaderAsync(string email, string name = "Bạn đọc", string phone = "0912345678", string code = "R-1")
    {
        var reader = new ReaderAccount
        {
            FullName = name, DateOfBirth = new DateOnly(2000, 1, 1), Email = email, PhoneNumber = phone,
            StudentOrStaffCode = code, Status = "Đang hoạt động", EmailConfirmed = true
        };
        reader.PasswordHash = new PasswordHasher<ReaderAccount>().HashPassword(reader, "Password123");
        db.ReaderAccounts.Add(reader);
        await db.SaveChangesAsync();
        return reader;
    }

    private ReaderRegistrationController ReaderController()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.5");
        var controller = new ReaderRegistrationController(registration, new ReaderRegistrationIpRateLimiter(),
            new ReaderPasswordResetService(db, new PasswordHasher<ReaderAccount>(), new NoEmailSender(), NullLogger<ReaderPasswordResetService>.Instance),
            new EphemeralDataProtectionProvider(), new AuditLogService(db, NullLogger<AuditLogService>.Instance), new NoOpEmailVerificationService())
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            Url = new FixedUrlHelper()
        };
        controller.TempData = new TempDataDictionary(context, new NoTempDataProvider());
        return controller;
    }

    private ReaderAccountController AdminController(string? role)
    {
        if (role is not null && !db.AdminAccounts.Any(a => a.Id == 1))
        {
            db.AdminAccounts.Add(new AdminAccount { Id = 1, Email = "admin@library.test", Role = role, IsActive = true, PasswordHash = "hash" });
            db.SaveChanges();
        }
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.5");
        var emailSender = new NoEmailSender();
        var verification = new ReaderEmailVerificationService(db, emailSender, registration, NullLogger<ReaderEmailVerificationService>.Instance);
        var controller = new ReaderAccountController(service, verification,
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

    /// <summary>Dùng nhật ký thật nhưng giả lập nhân sự đang đăng nhập với vai trò cho trước.</summary>
    private sealed class FixedStaffAuditLogService(AuditLogService inner, string? role) : IAuditLogService
    {
        public Task WriteAsync(string actor, string action, string target, string? ipAddress, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(actor, action, target, ipAddress, cancellationToken);
        public Task<IReadOnlyList<AuditLog>> GetRecentAsync(int limit, CancellationToken cancellationToken = default) => inner.GetRecentAsync(limit, cancellationToken);
        public Task<IReadOnlyList<AuditLog>> SearchAsync(AuditLogFilter filter, int limit, CancellationToken cancellationToken = default) => inner.SearchAsync(filter, limit, cancellationToken);
        public Task<IReadOnlyList<string>> GetActorsAsync(CancellationToken cancellationToken = default) => inner.GetActorsAsync(cancellationToken);
        public Task<AdminAccount?> GetSignedInStaffAsync(HttpRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(role is null ? null : new AdminAccount { Id = 1, Email = "admin@library.test", Role = role, IsActive = true });
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
        public string? Action(UrlActionContext actionContext) => "https://localhost/" + actionContext.Action;
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
