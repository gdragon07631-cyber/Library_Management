using System.Reflection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Net.Http.Headers;
using Project.Controllers;
using Project.Data;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class SecurityFixTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;
    private readonly AuditLogService auditLogService;
    private readonly EphemeralDataProtectionProvider dataProtection = new();

    public SecurityFixTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        auditLogService = new AuditLogService(db, NullLogger<AuditLogService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    // ---------- Hồ sơ bạn đọc chỉ thuộc về người đang đăng nhập ----------

    [Fact]
    public async Task AnonymousVisitorCannotOpenAReaderProfileById()
    {
        var reader = await AddReaderAsync("a@example.com");

        var result = await ReaderController(cookie: null).Profile(reader.Id);

        Assert.Equal(nameof(ReaderRegistrationController.Login), Assert.IsType<RedirectToActionResult>(result).ActionName);
    }

    [Fact]
    public async Task ReaderCannotOpenAnotherReadersProfile()
    {
        var me = await AddReaderAsync("me@example.com");
        var other = await AddReaderAsync("other@example.com");

        var result = await ReaderController(SessionCookie(me)).Profile(other.Id);

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.DoesNotContain("other@example.com", forbidden.Value?.ToString());
    }

    [Fact]
    public async Task ReaderStillOpensOwnProfileWithOrWithoutId()
    {
        var me = await AddReaderAsync("me@example.com");

        Assert.Equal("me@example.com", ProfileOf(await ReaderController(SessionCookie(me)).Profile(me.Id)).Email);
        Assert.Equal("me@example.com", ProfileOf(await ReaderController(SessionCookie(me)).Profile((int?)null)).Email);
    }

    [Fact]
    public async Task AnonymousCannotHoldADocumentForSomeoneElse()
    {
        var reader = await AddReaderAsync("a@example.com");

        var result = await ReaderController(cookie: null).HoldDocumentApi(1, readerId: reader.Id);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Empty(await db.BookHolds.ToListAsync());
    }

    [Fact]
    public async Task ReaderCannotHoldADocumentForAnotherReader()
    {
        var me = await AddReaderAsync("me@example.com");
        var other = await AddReaderAsync("other@example.com");

        var api = await ReaderController(SessionCookie(me)).HoldDocumentApi(1, readerId: other.Id);
        var page = await ReaderController(SessionCookie(me)).HoldDocument(1, readerId: other.Id);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(api).StatusCode);
        Assert.Equal(nameof(ReaderRegistrationController.Profile), Assert.IsType<RedirectToActionResult>(page).ActionName);
        Assert.Empty(await db.BookHolds.ToListAsync());
    }

    // ---------- Bộ lọc phân quyền cho chức năng nhân sự ----------

    [Theory]
    [InlineData(typeof(AuthorController), new[] { AccountRoles.SystemAdmin, AccountRoles.LibraryManager })]
    [InlineData(typeof(CategoryController), new[] { AccountRoles.SystemAdmin, AccountRoles.LibraryManager })]
    [InlineData(typeof(WarehouseController), new[] { AccountRoles.SystemAdmin, AccountRoles.LibraryManager })]
    [InlineData(typeof(ShelfController), new[] { AccountRoles.SystemAdmin, AccountRoles.LibraryManager })]
    [InlineData(typeof(LoanController), new[] { AccountRoles.Librarian, AccountRoles.SystemAdmin, AccountRoles.LibraryManager })]
    [InlineData(typeof(LoanBlockHistoryController), new[] { AccountRoles.LibraryManager })]
    [InlineData(typeof(WorkingScheduleController), new[] { AccountRoles.SystemAdmin, AccountRoles.LibraryManager })]
    [InlineData(typeof(BookController), new[] { AccountRoles.Librarian, AccountRoles.SystemAdmin, AccountRoles.LibraryManager })]
    public void LibraryControllersRequireTheSameRolesAsTheirPages(Type controller, string[] roles)
    {
        var attribute = controller.GetCustomAttribute<StaffOnlyAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(roles.Order(), attribute.Roles.Order());
    }

    [Fact]
    public void OnlyReaderFacingBookActionsArePublic()
    {
        var publicActions = typeof(BookController).GetMethods()
            .Where(method => method.GetCustomAttribute<PublicActionAttribute>() is not null)
            .Select(method => method.Name).Order();

        Assert.Equal(["Details", "GetBookDetailsApi", "Hold"], publicActions);
    }

    [Fact]
    public async Task AnonymousApiCallIsRejectedWith401()
    {
        var (result, ranAction) = await RunFilterAsync("/api/authors", "DELETE", cookie: null);

        Assert.False(ranAction);
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task AnonymousPagePostIsSentToStaffLogin()
    {
        var (result, ranAction) = await RunFilterAsync("/Category/Delete", "POST", cookie: null);

        Assert.False(ranAction);
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(("Account", "Login"), (redirect.ControllerName, redirect.ActionName));
    }

    [Fact]
    public async Task StaffWithAnotherRoleIsForbidden()
    {
        var librarian = await SignInStaffAsync(AccountRoles.Librarian);

        var (result, ranAction) = await RunFilterAsync("/api/authors", "GET", librarian);

        Assert.False(ranAction);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task LibrarianCanAccessLoanActions()
    {
        var librarian = await SignInStaffAsync(AccountRoles.Librarian);

        var loanRoles = typeof(LoanController).GetCustomAttribute<StaffOnlyAttribute>()!.Roles.ToArray();
        var (result, ranAction) = await RunFilterAsync("/api/loans", "GET", librarian, roles: loanRoles);

        Assert.True(ranAction);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(AccountRoles.Librarian)]
    [InlineData(AccountRoles.SystemAdmin)]
    public async Task OnlyLibraryManagerCanAccessLoanBlockHistory(string role)
    {
        var account = await SignInStaffAsync(role);
        var historyRoles = typeof(LoanBlockHistoryController).GetCustomAttribute<StaffOnlyAttribute>()!.Roles.ToArray();

        var (result, ranAction) = await RunFilterAsync("/api/loan-block-history", "GET", account, roles: historyRoles);

        Assert.False(ranAction);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Theory]
    [InlineData(AccountRoles.SystemAdmin)]
    [InlineData(AccountRoles.LibraryManager)]
    public async Task AllowedRolesStillReachTheAction(string role)
    {
        var (result, ranAction) = await RunFilterAsync("/api/authors", "POST", await SignInStaffAsync(role));

        Assert.True(ranAction);
        Assert.Null(result);
    }

    [Fact]
    public async Task LockedAccountIsRejected()
    {
        var cookie = await SignInStaffAsync(AccountRoles.SystemAdmin);
        await db.AdminAccounts.ExecuteUpdateAsync(setters => setters.SetProperty(account => account.IsActive, false));

        var (_, ranAction) = await RunFilterAsync("/api/authors", "POST", cookie);

        Assert.False(ranAction);
    }

    [Fact]
    public async Task PublicActionSkipsTheCheck()
    {
        var (result, ranAction) = await RunFilterAsync("/Book/Details/1", "GET", cookie: null, isPublic: true);

        Assert.True(ranAction);
        Assert.Null(result);
    }

    private async Task<(IActionResult? Result, bool RanAction)> RunFilterAsync(
        string path, string method, string? cookie, bool isPublic = false, string[]? roles = null)
    {
        var services = new ServiceCollection().AddSingleton<IAuditLogService>(auditLogService).BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };
        httpContext.Request.Path = path;
        httpContext.Request.Method = method;
        if (cookie is not null) httpContext.Request.Headers.Cookie = cookie;

        var descriptor = new ActionDescriptor { EndpointMetadata = isPublic ? [new PublicActionAttribute()] : [] };
        var context = new ActionExecutingContext(
            new ActionContext(httpContext, new RouteData(), descriptor),
            [], new Dictionary<string, object?>(), controller: new object());

        var ranAction = false;
        await new StaffOnlyAttribute(roles ?? [AccountRoles.SystemAdmin, AccountRoles.LibraryManager]).OnActionExecutionAsync(context, () =>
        {
            ranAction = true;
            return Task.FromResult(new ActionExecutedContext(context, [], controller: new object()));
        });
        return (context.Result, ranAction);
    }

    private async Task<string> SignInStaffAsync(string role)
    {
        var account = new AdminAccount { Email = $"{role}-{Guid.NewGuid():N}@example.com", Role = role, IsActive = true, PasswordHash = "x" };
        var token = Guid.NewGuid().ToString("N");
        db.RefreshTokens.Add(new RefreshToken
        {
            AdminAccount = account,
            TokenHash = TokenService.HashRefreshToken(token),
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(1)
        });
        await db.SaveChangesAsync();
        return $"admin_refresh={token}";
    }

    private async Task<ReaderAccount> AddReaderAsync(string email)
    {
        var reader = new ReaderAccount
        {
            FullName = "Test Reader",
            DateOfBirth = new DateOnly(2000, 1, 1),
            Email = email,
            PhoneNumber = "0912345678",
            StudentOrStaffCode = "R-" + email,
            Status = "Đang hoạt động"
        };
        reader.PasswordHash = new PasswordHasher<ReaderAccount>().HashPassword(reader, "password123");
        db.ReaderAccounts.Add(reader);
        await db.SaveChangesAsync();
        return reader;
    }

    private string SessionCookie(ReaderAccount reader)
    {
        var context = new DefaultHttpContext();
        ReaderSessionCookies.Append(context, dataProtection, reader);
        return string.Join("; ", SetCookieHeaderValue.ParseList(context.Response.Headers.SetCookie.Select(value => value!).ToList())
            .Select(cookie => $"{cookie.Name}={cookie.Value}"));
    }

    private static ReaderProfileViewModel ProfileOf(IActionResult result) =>
        Assert.IsType<ReaderProfileViewModel>(Assert.IsType<ViewResult>(result).Model);

    private ReaderRegistrationController ReaderController(string? cookie)
    {
        var registration = new ReaderRegistrationService(db, new PasswordHasher<ReaderAccount>(), NullLogger<ReaderRegistrationService>.Instance);
        var controller = new ReaderRegistrationController(registration, new ReaderRegistrationIpRateLimiter(),
            new ReaderPasswordResetService(db, new PasswordHasher<ReaderAccount>(), new NoEmailSender(), NullLogger<ReaderPasswordResetService>.Instance),
            dataProtection, auditLogService);
        var context = new DefaultHttpContext();
        if (cookie is not null) context.Request.Headers.Cookie = cookie;
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, new NoTempDataProvider());
        return controller;
    }

    private sealed class NoEmailSender : IEmailSender
    {
        public Task SendAsync(string recipient, string subject, string htmlBody, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
