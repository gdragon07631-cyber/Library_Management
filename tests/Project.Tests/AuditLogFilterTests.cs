using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Project.Controllers;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class AuditLogFilterTests : IDisposable
{
    private const string Admin = "admin@example.com";
    private const string Librarian = "librarian@example.com";
    private const string Reader = "reader@example.com";

    private static readonly DateOnly Day10 = new(2026, 9, 10);
    private static readonly DateOnly Day15 = new(2026, 9, 15);
    private static readonly DateOnly Day16 = new(2026, 9, 16);
    private static readonly DateOnly Day20 = new(2026, 9, 20);

    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;
    private readonly AuditLogService service;

    public AuditLogFilterTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        service = new AuditLogService(db, NullLogger<AuditLogService>.Instance);

        // Local-time placement matters: the screen shows and filters by local dates.
        AddLog(Day10, 9, Admin, AuditActions.Login);
        AddLog(Day10, 10, Admin, AuditActions.UpdateLoanPolicy);
        AddLog(Day15, 0, Librarian, AuditActions.Login);                     // 00:00 đầu ngày 15
        AddLog(Day15, 11, Librarian, AuditActions.IssueCard);
        AddLog(Day15, 23, Reader, AuditActions.CreateAccount, minute: 59);    // 23:59 cuối ngày 15
        AddLog(Day16, 0, Reader, AuditActions.Login);                        // 00:00 ngày 16
        AddLog(Day20, 8, Reader, AuditActions.UpdateAccount);
        db.SaveChanges();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    // ---------- Khoảng ngày ----------

    [Fact]
    public async Task DateRangeIncludesWholeStartAndEndDays()
    {
        var logs = await service.SearchAsync(new AuditLogFilter { FromDate = Day15, ToDate = Day15 }, 500);

        Assert.Equal([AuditActions.CreateAccount, AuditActions.IssueCard, AuditActions.Login], logs.Select(log => log.Action));
        Assert.All(logs, log => Assert.Equal(Day15, LocalDate(log)));
    }

    [Fact]
    public async Task FromDateOnlyExcludesEarlierDays()
    {
        var logs = await service.SearchAsync(new AuditLogFilter { FromDate = Day16 }, 500);

        Assert.Equal(2, logs.Count);
        Assert.All(logs, log => Assert.True(LocalDate(log) >= Day16));
    }

    [Fact]
    public async Task ToDateOnlyExcludesLaterDays()
    {
        var logs = await service.SearchAsync(new AuditLogFilter { ToDate = Day10 }, 500);

        Assert.Equal(2, logs.Count);
        Assert.All(logs, log => Assert.Equal(Admin, log.Actor));
    }

    // ---------- Người thực hiện ----------

    [Theory]
    [InlineData(Admin, 2)]
    [InlineData(Librarian, 2)]
    [InlineData(Reader, 3)]
    public async Task ActorFilterReturnsOnlyThatActor(string actor, int expectedCount)
    {
        var logs = await service.SearchAsync(new AuditLogFilter { Actor = actor }, 500);

        Assert.Equal(expectedCount, logs.Count);
        Assert.All(logs, log => Assert.Equal(actor, log.Actor));
    }

    [Fact]
    public async Task ActorOptionsAreDistinctAndSorted()
    {
        Assert.Equal([Admin, Librarian, Reader], await service.GetActorsAsync());
    }

    // ---------- Loại hành động ----------

    [Theory]
    [InlineData(AuditActions.Login, 3)]
    [InlineData(AuditActions.CreateAccount, 1)]
    [InlineData(AuditActions.UpdateAccount, 1)]
    [InlineData(AuditActions.IssueCard, 1)]
    [InlineData(AuditActions.UpdateLoanPolicy, 1)]
    public async Task ActionFilterReturnsOnlyThatAction(string action, int expectedCount)
    {
        var logs = await service.SearchAsync(new AuditLogFilter { Action = action }, 500);

        Assert.Equal(expectedCount, logs.Count);
        Assert.All(logs, log => Assert.Equal(action, log.Action));
    }

    [Fact]
    public void EveryActionTypeIsOfferedInTheFilter()
    {
        Assert.Equal(
            [AuditActions.Login, AuditActions.CreateAccount, AuditActions.UpdateAccount, AuditActions.IssueCard, AuditActions.UpdateLoanPolicy, AuditActions.BlockLoanLimit, AuditActions.BlockLoanPolicy, AuditActions.OverrideLoanPolicy, AuditActions.ReturnBookLoan],
            new AuditLogIndexViewModel().Actions);
    }

    // ---------- Kết hợp ----------

    [Fact]
    public async Task DateActorAndActionAreCombined()
    {
        var logs = await service.SearchAsync(new AuditLogFilter
        {
            FromDate = Day15,
            ToDate = Day20,
            Actor = Reader,
            Action = AuditActions.Login
        }, 500);

        var log = Assert.Single(logs);
        Assert.Equal(Reader, log.Actor);
        Assert.Equal(AuditActions.Login, log.Action);
        Assert.Equal(Day16, LocalDate(log));
    }

    [Fact]
    public async Task CombinationWithoutMatchesReturnsNothing()
    {
        Assert.Empty(await service.SearchAsync(new AuditLogFilter { Actor = Admin, Action = AuditActions.IssueCard }, 500));
        Assert.Empty(await service.SearchAsync(new AuditLogFilter { FromDate = Day10, ToDate = Day10, Actor = Reader }, 500));
    }

    // ---------- Màn hình ----------

    [Fact]
    public async Task ScreenShowsOnlyMatchingLogs()
    {
        var model = await IndexAsync(new AuditLogFilter { Actor = Librarian, Action = AuditActions.IssueCard });

        var log = Assert.Single(model.Logs);
        Assert.Equal(Librarian, log.Actor);
        Assert.Null(model.ErrorMessage);
        Assert.Equal([Admin, Librarian, Reader], model.Actors);
    }

    [Fact]
    public async Task ScreenWithNoMatchesHasEmptyListAndNoError()
    {
        var model = await IndexAsync(new AuditLogFilter { FromDate = new DateOnly(2027, 1, 1) });

        Assert.Empty(model.Logs);
        Assert.Null(model.ErrorMessage);
        Assert.False(model.Filter.IsEmpty);
    }

    [Fact]
    public async Task ClearingTheFilterShowsAllLogsAgain()
    {
        var filtered = await IndexAsync(new AuditLogFilter { Actor = Admin });
        Assert.Equal(2, filtered.Logs.Count);

        // "Xóa bộ lọc" là liên kết tới /AuditLog không kèm tham số.
        var cleared = await IndexAsync(null);

        Assert.Equal(7, cleared.Logs.Count);
        Assert.True(cleared.Filter.IsEmpty);
        Assert.Equal((await service.GetRecentAsync(500)).Select(log => log.Id), cleared.Logs.Select(log => log.Id));
    }

    [Fact]
    public async Task StartDateAfterEndDateShowsError()
    {
        var model = await IndexAsync(new AuditLogFilter { FromDate = Day20, ToDate = Day10 });

        Assert.Equal("Từ ngày không được lớn hơn đến ngày.", model.ErrorMessage);
        Assert.Empty(model.Logs);
    }

    [Fact]
    public async Task NonAdminStillCannotOpenTheScreen()
    {
        var controller = CreateController(AccountRoles.Librarian);

        var denied = Assert.IsType<ViewResult>(await controller.Index(new AuditLogFilter { Actor = Admin }));
        Assert.Equal("AccessDenied", denied.ViewName);
        Assert.Null(denied.Model);
    }

    private void AddLog(DateOnly localDate, int hour, string actor, string action, int minute = 0) =>
        db.AuditLogs.Add(new AuditLog
        {
            OccurredAtUtc = AuditLogService.StartOfLocalDayUtc(localDate).AddHours(hour).AddMinutes(minute),
            Actor = actor,
            Action = action,
            Target = $"{action} bởi {actor}",
            IpAddress = "127.0.0.1"
        });

    private static DateOnly LocalDate(AuditLog log) =>
        DateOnly.FromDateTime(DateTime.SpecifyKind(log.OccurredAtUtc, DateTimeKind.Utc).ToLocalTime());

    private async Task<AuditLogIndexViewModel> IndexAsync(AuditLogFilter? filter)
    {
        var result = Assert.IsType<ViewResult>(await CreateController(AccountRoles.SystemAdmin).Index(filter));
        return Assert.IsType<AuditLogIndexViewModel>(result.Model);
    }

    private AuditLogController CreateController(string role)
    {
        var account = new AdminAccount { Email = $"{role}-{Guid.NewGuid():N}@staff.example.com", Role = role, PasswordHash = "x" };
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

        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"admin_refresh={token}";
        return new AuditLogController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            Url = new FixedUrlHelper()
        };
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
}
