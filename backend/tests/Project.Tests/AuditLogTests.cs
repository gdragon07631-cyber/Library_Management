using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Net.Http.Headers;
using Project.Controllers;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class AuditLogTests : IDisposable
{
    private const string ClientIp = "203.0.113.7";
    private const string AdminEmail = "admin@example.com";
    private const string LibrarianEmail = "librarian@example.com";

    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;
    private readonly AuditLogService auditLogService;
    private readonly ReaderRegistrationService registrationService;
    private readonly EphemeralDataProtectionProvider dataProtection = new();

    public AuditLogTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        auditLogService = new AuditLogService(db, NullLogger<AuditLogService>.Instance);
        registrationService = new ReaderRegistrationService(db, new PasswordHasher<ReaderAccount>(),
            NullLogger<ReaderRegistrationService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    // ---------- Đăng nhập ----------

    [Fact]
    public async Task SuccessfulStaffLoginWritesExactlyOneLog()
    {
        var admin = await AddStaffAsync(AdminEmail, AccountRoles.SystemAdmin, "admin-password");
        var authentication = new AuthenticationService(db, new PasswordHasher<AdminAccount>(),
            NullLogger<AuthenticationService>.Instance);

        var outcome = await authentication.LoginAsync(AdminEmail, "admin-password", ClientIp, default, AccountRoles.SystemAdmin);

        Assert.Equal(LoginResult.LoginSuccess, outcome.Result);
        var log = await SingleLogAsync();
        Assert.Equal(AdminEmail, log.Actor);
        Assert.Equal(AuditActions.Login, log.Action);
        Assert.Equal($"Tài khoản quản trị hệ thống #{admin.Id} ({AdminEmail})", log.Target);
        Assert.Equal(ClientIp, log.IpAddress);
        Assert.InRange(log.OccurredAtUtc, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task FailedStaffLoginIsLoggedSeparatelyFromSuccessfulLogins()
    {
        await AddStaffAsync(AdminEmail, AccountRoles.SystemAdmin, "admin-password");
        var authentication = new AuthenticationService(db, new PasswordHasher<AdminAccount>(),
            NullLogger<AuthenticationService>.Instance);

        await authentication.LoginAsync(AdminEmail, "wrong-password", ClientIp, default, AccountRoles.SystemAdmin);

        // Không tạo bản ghi "Đăng nhập" (chỉ dành cho đăng nhập thành công) mà ghi riêng "Đăng nhập thất bại".
        var log = Assert.Single(await db.AuditLogs.ToListAsync());
        Assert.Equal(AuditActions.LoginFailed, log.Action);
        Assert.Equal(AdminEmail, log.Actor);
        Assert.Contains("sai mật khẩu (lần 1/5)", log.Target);
    }

    [Fact]
    public async Task SuccessfulReaderLoginWritesExactlyOneLog()
    {
        var reader = await AddReaderAsync("reader@example.com", "reader-password", "Đang hoạt động");
        var controller = CreateReaderController();

        Assert.IsType<RedirectToActionResult>(await controller.Login("reader@example.com", "reader-password"));

        var log = await SingleLogAsync();
        Assert.Equal("reader@example.com", log.Actor);
        Assert.Equal(AuditActions.Login, log.Action);
        Assert.Equal($"Tài khoản bạn đọc #{reader.Id} (reader@example.com)", log.Target);
        Assert.Equal(ClientIp, log.IpAddress);
    }

    // ---------- Tạo tài khoản ----------

    [Fact]
    public async Task ReaderRegistrationWritesExactlyOneLog()
    {
        var controller = CreateReaderController();

        Assert.IsType<RedirectToActionResult>(await controller.Register(NewRegistration()));

        var reader = await db.ReaderAccounts.SingleAsync();
        var log = await SingleLogAsync();
        Assert.Equal("new.reader@example.com", log.Actor);
        Assert.Equal(AuditActions.CreateAccount, log.Action);
        Assert.Equal($"Tài khoản bạn đọc #{reader.Id} (new.reader@example.com)", log.Target);
        Assert.Equal(ClientIp, log.IpAddress);
    }

    [Fact]
    public async Task RegistrationByStaffRecordsStaffAsActor()
    {
        var librarian = await AddStaffAsync(LibrarianEmail, AccountRoles.Librarian, "librarian-password");
        var controller = CreateReaderController(await CreateStaffCookieAsync(librarian));

        await controller.Register(NewRegistration());

        Assert.Equal(LibrarianEmail, (await SingleLogAsync()).Actor);
    }

    [Fact]
    public async Task DuplicateRegistrationWritesNoLog()
    {
        await AddReaderAsync("new.reader@example.com", "reader-password", "Chờ duyệt");

        Assert.IsType<ViewResult>(await CreateReaderController().Register(NewRegistration()));

        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    // ---------- Sửa tài khoản ----------

    [Fact]
    public async Task ProfileUpdateWritesExactlyOneLog()
    {
        var reader = await AddReaderAsync("reader@example.com", "reader-password", "Đang hoạt động");
        var controller = CreateReaderController(CreateReaderSessionCookie(reader));

        var result = await controller.Profile(new ReaderProfileViewModel
        {
            PhoneNumber = "0987654321",
            Address = "12 Nguyễn Trãi",
            Email = "reader@example.com"
        });

        Assert.IsType<RedirectToActionResult>(result);
        var log = await SingleLogAsync();
        Assert.Equal("reader@example.com", log.Actor);
        Assert.Equal(AuditActions.UpdateAccount, log.Action);
        Assert.Equal($"Tài khoản bạn đọc #{reader.Id} (reader@example.com)", log.Target);
        Assert.Equal(ClientIp, log.IpAddress);
    }

    // ---------- Cấp thẻ ----------

    [Fact]
    public async Task IssuingCardWritesExactlyOneLog()
    {
        var librarian = await AddStaffAsync(LibrarianEmail, AccountRoles.Librarian, "librarian-password");
        var reader = await AddReaderAsync("reader@example.com", "reader-password", "Chờ duyệt");
        var cardType = new LibraryCardType { Name = "Sinh viên" };
        db.LibraryCardTypes.Add(cardType);
        await db.SaveChangesAsync();
        var controller = new ReaderApprovalController(registrationService, db, auditLogService);
        Attach(controller, await CreateStaffCookieAsync(librarian));

        await controller.Approve(new ApproveReaderViewModel
        {
            ReaderAccountId = reader.Id,
            LibraryCardTypeId = cardType.Id,
            IssuedOn = new DateOnly(2026, 9, 29),
            ExpiresOn = new DateOnly(2027, 9, 29)
        });

        var card = await db.LibraryCards.SingleAsync();
        var log = await SingleLogAsync();
        Assert.Equal(LibrarianEmail, log.Actor);
        Assert.Equal(AuditActions.IssueCard, log.Action);
        Assert.Equal($"Thẻ {card.CardCode} – bạn đọc #{reader.Id}", log.Target);
        Assert.Equal(ClientIp, log.IpAddress);
    }

    // ---------- Sửa chính sách mượn ----------

    [Fact]
    public async Task UpdatingLoanPolicyWritesExactlyOneLog()
    {
        var admin = await AddStaffAsync(AdminEmail, AccountRoles.SystemAdmin, "admin-password");
        var controller = new LoanPolicyController(db, auditLogService);
        Attach(controller, await CreateStaffCookieAsync(admin));

        Assert.IsType<RedirectToActionResult>(await controller.Index(new LoanPolicyViewModel { LoanDays = 21, DailyOverdueFee = 2_500m }));

        var savedPolicy = await db.LoanPolicies.AsNoTracking().SingleAsync();
        Assert.Equal(21, savedPolicy.LoanDays);
        Assert.Equal(2_500m, savedPolicy.DailyOverdueFee);
        var log = await SingleLogAsync();
        Assert.Equal(AdminEmail, log.Actor);
        Assert.Equal(AuditActions.UpdateLoanPolicy, log.Action);
        Assert.Contains("số ngày mượn 14 → 21", log.Target);
        Assert.Contains("phí trễ hạn/ngày", log.Target);
        Assert.Equal(ClientIp, log.IpAddress);
    }

    [Fact]
    public async Task LoanPolicyIsOnlyEditableBySystemAdmin()
    {
        var librarian = await AddStaffAsync(LibrarianEmail, AccountRoles.Librarian, "librarian-password");
        var controller = new LoanPolicyController(db, auditLogService);
        Attach(controller, await CreateStaffCookieAsync(librarian));

        await controller.Index(new LoanPolicyViewModel { LoanDays = 30 });

        Assert.Equal(LoanPolicy.DefaultLoanDays, (await db.LoanPolicies.AsNoTracking().SingleAsync()).LoanDays);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task SavingUnchangedLoanPolicyWritesNoLog()
    {
        var admin = await AddStaffAsync(AdminEmail, AccountRoles.SystemAdmin, "admin-password");
        var controller = new LoanPolicyController(db, auditLogService);
        Attach(controller, await CreateStaffCookieAsync(admin));

        await controller.Index(new LoanPolicyViewModel { LoanDays = LoanPolicy.DefaultLoanDays });

        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    // ---------- Màn hình nhật ký ----------

    [Fact]
    public async Task SystemAdminSeesLogsNewestFirst()
    {
        var admin = await AddStaffAsync(AdminEmail, AccountRoles.SystemAdmin, "admin-password");
        await auditLogService.WriteAsync("a@example.com", AuditActions.Login, "Tài khoản A", ClientIp);
        await auditLogService.WriteAsync("b@example.com", AuditActions.IssueCard, "Thẻ B", "::ffff:10.0.0.5");
        var controller = new AuditLogController(auditLogService);
        Attach(controller, await CreateStaffCookieAsync(admin));

        var logs = Assert.IsType<AuditLogIndexViewModel>(
            Assert.IsType<ViewResult>(await controller.Index()).Model).Logs;

        Assert.Equal(["b@example.com", "a@example.com"], logs.Select(log => log.Actor));
        Assert.Equal("10.0.0.5", logs[0].IpAddress);
    }

    [Fact]
    public async Task NonAdminCannotViewLogs()
    {
        var librarian = await AddStaffAsync(LibrarianEmail, AccountRoles.Librarian, "librarian-password");
        var controller = new AuditLogController(auditLogService);
        Attach(controller, await CreateStaffCookieAsync(librarian));

        var denied = Assert.IsType<ViewResult>(await controller.Index());
        Assert.Equal("AccessDenied", denied.ViewName);
        Assert.Equal(StatusCodes.Status403Forbidden, controller.Response.StatusCode);
    }

    private async Task<AuditLog> SingleLogAsync() => Assert.Single(await db.AuditLogs.AsNoTracking().ToListAsync());

    private async Task<AdminAccount> AddStaffAsync(string email, string role, string password)
    {
        var account = new AdminAccount { Email = email, Role = role, IsActive = true };
        account.PasswordHash = new PasswordHasher<AdminAccount>().HashPassword(account, password);
        db.AdminAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private async Task<ReaderAccount> AddReaderAsync(string email, string password, string status)
    {
        var reader = new ReaderAccount
        {
            FullName = "Test Reader",
            DateOfBirth = new DateOnly(2000, 1, 1),
            Email = email,
            PhoneNumber = "0912345678",
            StudentOrStaffCode = "R-" + email,
            Status = status
        };
        reader.PasswordHash = new PasswordHasher<ReaderAccount>().HashPassword(reader, password);
        db.ReaderAccounts.Add(reader);
        await db.SaveChangesAsync();
        return reader;
    }

    private static ReaderRegistrationViewModel NewRegistration() => new()
    {
        FullName = "Nguyen Van B",
        DateOfBirth = new DateOnly(2001, 2, 3),
        Email = "new.reader@example.com",
        PhoneNumber = "0911222333",
        StudentOrStaffCode = "SV-NEW",
        Password = "password123",
        ConfirmPassword = "password123"
    };

    /// <summary>Stores a refresh token for the staff account and returns the matching Cookie header.</summary>
    private async Task<string> CreateStaffCookieAsync(AdminAccount account)
    {
        var token = Guid.NewGuid().ToString("N");
        db.RefreshTokens.Add(new RefreshToken
        {
            AdminAccountId = account.Id,
            TokenHash = TokenService.HashRefreshToken(token),
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(1)
        });
        await db.SaveChangesAsync();
        return $"admin_refresh={token}";
    }

    private string CreateReaderSessionCookie(ReaderAccount reader)
    {
        var context = new DefaultHttpContext();
        ReaderSessionCookies.Append(context, dataProtection, reader);
        return string.Join("; ", SetCookieHeaderValue.ParseList(context.Response.Headers.SetCookie.Select(value => value!).ToList())
            .Select(cookie => $"{cookie.Name}={cookie.Value}"));
    }

    private ReaderRegistrationController CreateReaderController(string? cookieHeader = null)
    {
        var controller = new ReaderRegistrationController(registrationService, new ReaderRegistrationIpRateLimiter(),
            new ReaderPasswordResetService(db, new PasswordHasher<ReaderAccount>(), new NoEmailSender(),
                NullLogger<ReaderPasswordResetService>.Instance),
            dataProtection, auditLogService, new NoOpEmailVerificationService());
        Attach(controller, cookieHeader);
        return controller;
    }

    private static void Attach(Controller controller, string? cookieHeader)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ClientIp);
        if (cookieHeader is not null) context.Request.Headers.Cookie = cookieHeader;
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, new NoTempDataProvider());
        controller.Url = new FixedUrlHelper();
    }

    private sealed class NoEmailSender : IEmailSender
    {
        public Task SendAsync(string recipient, string subject, string htmlBody, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
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
