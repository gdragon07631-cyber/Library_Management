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
    [InlineData(AccountRoles.SystemAdmin)]
    [InlineData(AccountRoles.LibraryManager)]
    public async Task SystemAdminAndLibraryManagerCanOpenReaderAccounts(string role)
    {
        var reader = await AddReaderAsync("open@example.com", code: "R-OPEN");
        var controller = AdminController(role);

        Assert.IsType<ViewResult>(await controller.Index(null, null));
        Assert.IsType<ViewResult>(await controller.Details(reader.Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(AccountRoles.Librarian)]
    public async Task OtherRolesCannotOpenReaderAccounts(string? role)
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

    [Fact]
    public async Task CardLockPersistsTimedMetadataAndExpiresAutomatically()
    {
        var (reader, card, admin) = await AddReaderWithCardAndAdminAsync();
        var expiry = DateTime.UtcNow.AddHours(1);

        var result = await service.SetCardLockedAsync(reader.Id, "  Vi phạm nội quy  ", expiry, false, admin.Id);

        Assert.True(result.IsSuccess);
        db.ChangeTracker.Clear();
        var stored = await db.LibraryCards.SingleAsync(item => item.Id == card.Id);
        Assert.Equal("Đang bị khoá", stored.Status);
        Assert.Equal("Vi phạm nội quy", stored.LockReason);
        Assert.NotNull(stored.LockedAtUtc);
        Assert.Equal(expiry, stored.LockExpiresAtUtc);
        Assert.False(stored.IsLockUntilManualUnlock);
        Assert.Equal(admin.Id, stored.LockedByAdminAccountId);
        Assert.True(LibraryCardStatusHelper.IsCardLocked(stored, DateTime.UtcNow));
        Assert.False(LibraryCardStatusHelper.IsCardLocked(stored, expiry.AddSeconds(1)));
        Assert.True(LibraryCardStatusHelper.IsCardAvailable(stored, expiry.AddSeconds(1)));
        Assert.Equal("Đang hoạt động", LibraryCardStatusHelper.GetEffectiveStatus(stored, expiry.AddSeconds(1)));
    }

    [Fact]
    public async Task ManualCardLockHasNoExpiryAndCannotBeBypassedByRepeatingRequest()
    {
        var (reader, card, admin) = await AddReaderWithCardAndAdminAsync();

        var first = await service.SetCardLockedAsync(reader.Id, "Vi phạm nghiêm trọng", null, true, admin.Id);
        var second = await service.SetCardLockedAsync(reader.Id, "Lý do khác", null, true, admin.Id);

        Assert.True(first.IsSuccess);
        Assert.Equal(ReaderAccountAdminStatus.NoChange, second.Status);
        db.ChangeTracker.Clear();
        var stored = await db.LibraryCards.SingleAsync(item => item.Id == card.Id);
        Assert.True(stored.IsLockUntilManualUnlock);
        Assert.Null(stored.LockExpiresAtUtc);
        Assert.True(LibraryCardStatusHelper.IsCardLocked(stored, DateTime.UtcNow.AddYears(10)));
        Assert.Equal("Vi phạm nghiêm trọng", stored.LockReason);
    }

    [Fact]
    public async Task CardLockRejectsBlankReasonAndInvalidExpiryWithoutChangingCard()
    {
        var (reader, card, admin) = await AddReaderWithCardAndAdminAsync();

        var blankReason = await service.SetCardLockedAsync(reader.Id, " \t ", DateTime.UtcNow.AddHours(1), false, admin.Id);
        var invalidExpiry = await service.SetCardLockedAsync(reader.Id, "Vi phạm", DateTime.UtcNow.AddMinutes(-1), false, admin.Id);

        Assert.Equal(ReaderAccountAdminStatus.MissingReason, blankReason.Status);
        Assert.Equal(ReaderAccountAdminStatus.MissingReason, invalidExpiry.Status);
        db.ChangeTracker.Clear();
        var stored = await db.LibraryCards.SingleAsync(item => item.Id == card.Id);
        Assert.Equal("Đang hoạt động", stored.Status);
        Assert.Null(stored.LockedAtUtc);
        Assert.Null(stored.LockReason);
    }

    [Fact]
    public async Task LibraryManagerCanLockCardThroughController()
    {
        var (reader, card, admin) = await AddReaderWithCardAndAdminAsync();
        var expiry = DateTime.Now.AddDays(3);

        var result = await AdminController(AccountRoles.LibraryManager)
            .SetCardLocked(reader.Id, "Làm hỏng sách", "temporary", expiry);

        Assert.IsType<RedirectToActionResult>(result);
        db.ChangeTracker.Clear();
        var stored = await db.LibraryCards.SingleAsync(item => item.Id == card.Id);
        Assert.Equal("Đang bị khoá", stored.Status);
        Assert.Equal("Làm hỏng sách", stored.LockReason);
        Assert.False(stored.IsLockUntilManualUnlock);
        Assert.NotNull(stored.LockExpiresAtUtc);
        Assert.Equal(admin.Id, stored.LockedByAdminAccountId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(AccountRoles.Librarian)]
    public async Task UnauthorizedRolesCannotCallCardLockActionDirectly(string? role)
    {
        var (reader, card, _) = await AddReaderWithCardAndAdminAsync();

        var controller = AdminController(role, out var context);
        var result = await controller.SetCardLocked(reader.Id, "Lý do", "manual", null);

        if (role is null) Assert.IsType<RedirectToActionResult>(result);
        else
        {
            Assert.IsType<ViewResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        }
        db.ChangeTracker.Clear();
        var stored = await db.LibraryCards.SingleAsync(item => item.Id == card.Id);
        Assert.Equal("Đang hoạt động", stored.Status);
        Assert.Null(stored.LockedAtUtc);
    }

    [Fact]
    public async Task LibraryManagerCannotCallSystemAdminOnlyAccountActions()
    {
        var reader = await AddReaderAsync("manager-cannot-edit@example.com", code: "R-MANAGER");
        var form = ReaderAccountAdminService.ToForm(reader);
        form.PhoneNumber = "0999888777";

        var editController = AdminController(AccountRoles.LibraryManager, out var editContext);
        Assert.IsType<ViewResult>(await editController.Edit(reader.Id, form));
        Assert.Equal(StatusCodes.Status403Forbidden, editContext.Response.StatusCode);

        var lockController = AdminController(AccountRoles.LibraryManager, out var lockContext);
        Assert.IsType<ViewResult>(await lockController.SetLocked(reader.Id, true, "Lý do"));
        Assert.Equal(StatusCodes.Status403Forbidden, lockContext.Response.StatusCode);

        db.ChangeTracker.Clear();
        var stored = await db.ReaderAccounts.SingleAsync(item => item.Id == reader.Id);
        Assert.Equal("0912345678", stored.PhoneNumber);
        Assert.False(stored.IsLocked);
    }

    private async Task<(ReaderAccount Reader, LibraryCard Card, AdminAccount Admin)> AddReaderWithCardAndAdminAsync()
    {
        var reader = await AddReaderAsync("card-lock@example.com", code: "R-CARD-LOCK");
        var cardType = new LibraryCardType { Name = "Thẻ khoá kiểm thử" };
        var card = new LibraryCard
        {
            CardCode = "CARD-LOCK-TEST",
            ReaderAccount = reader,
            LibraryCardType = cardType,
            IssuedOn = DateOnly.FromDateTime(DateTime.Today.AddYears(-1)),
            ExpiresOn = DateOnly.FromDateTime(DateTime.Today.AddYears(1)),
            Status = "Đang hoạt động"
        };
        var admin = new AdminAccount
        {
            Email = "card-lock-admin@example.test",
            Role = AccountRoles.SystemAdmin,
            IsActive = true,
            PasswordHash = "hash"
        };
        db.AddRange(card, admin);
        await db.SaveChangesAsync();
        return (reader, card, admin);
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

    private ReaderAccountController AdminController(string? role) => AdminController(role, out _);

    private ReaderAccountController AdminController(string? role, out DefaultHttpContext context)
    {
        context = new DefaultHttpContext();
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
