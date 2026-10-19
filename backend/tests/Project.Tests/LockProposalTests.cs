using System.ComponentModel.DataAnnotations;
using System.Net;
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

/// <summary>S4-04 lát 2: cấu hình ngưỡng nợ phí và đề xuất khoá thẻ theo quá hạn trên 30 ngày / nợ vượt ngưỡng.</summary>
public sealed class LockProposalTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;
    private readonly DateOnly today = DateOnly.FromDateTime(DateTime.Today);
    private int sequence;

    public LockProposalTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    // ---------- Cấu hình ngưỡng nợ phí ----------

    [Fact]
    public async Task ValidThresholdIsPersistedAndShownWhenReopened()
    {
        var result = await PolicyController(AccountRoles.LibraryManager)
            .Index(new LoanPolicyViewModel { LoanDays = LoanPolicy.DefaultLoanDays, DebtLockThreshold = 200000m });

        Assert.IsType<RedirectToActionResult>(result);
        db.ChangeTracker.Clear();
        Assert.Equal(200000m, (await db.LoanPolicies.AsNoTracking().SingleAsync()).DebtLockThreshold);

        var view = Assert.IsType<ViewResult>(await PolicyController(AccountRoles.LibraryManager).Index());
        Assert.Equal(200000m, Assert.IsType<LoanPolicyViewModel>(view.Model).DebtLockThreshold);
        var api = Assert.IsType<OkObjectResult>(await PolicyController(AccountRoles.LibraryManager).GetDebtThresholdApi());
        Assert.Contains("200000", System.Text.Json.JsonSerializer.Serialize(api.Value));
        Assert.Contains(await db.AuditLogs.Select(log => log.Target).ToListAsync(), target => target == "Chính sách mượn: ngưỡng nợ phí 500000 → 200000");
    }

    [Fact]
    public async Task ThresholdApiUpdatesValueAndRejectsNegativeOrMissingValues()
    {
        var controller = PolicyController(AccountRoles.LibraryManager);
        Assert.IsType<OkObjectResult>(await controller.UpdateDebtThresholdApi(new UpdateDebtThresholdRequest { DebtLockThreshold = 150000m }));
        Assert.IsType<BadRequestObjectResult>(await PolicyController(AccountRoles.LibraryManager).UpdateDebtThresholdApi(new UpdateDebtThresholdRequest { DebtLockThreshold = -1m }));
        Assert.IsType<BadRequestObjectResult>(await PolicyController(AccountRoles.LibraryManager).UpdateDebtThresholdApi(new UpdateDebtThresholdRequest()));
        Assert.IsType<BadRequestObjectResult>(await PolicyController(AccountRoles.LibraryManager).UpdateDebtThresholdApi(null));

        db.ChangeTracker.Clear();
        Assert.Equal(150000m, (await db.LoanPolicies.AsNoTracking().SingleAsync()).DebtLockThreshold);
    }

    [Fact]
    public void FormModelRejectsNegativeThreshold()
    {
        var model = new LoanPolicyViewModel { LoanDays = 14, DebtLockThreshold = -5000m };
        var errors = new List<ValidationResult>();

        Assert.False(Validator.TryValidateObject(model, new ValidationContext(model), errors, validateAllProperties: true));
        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(LoanPolicyViewModel.DebtLockThreshold)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(AccountRoles.Librarian)]
    public async Task UnauthorizedStaffCannotReadOrChangeThresholdOrProposals(string? role)
    {
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(await PolicyController(role).GetDebtThresholdApi()).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(await PolicyController(role).GetLockProposalsApi()).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(
            await PolicyController(role).UpdateDebtThresholdApi(new UpdateDebtThresholdRequest { DebtLockThreshold = 1m })).StatusCode);
        Assert.IsType<RedirectToActionResult>(await PolicyController(role)
            .Index(new LoanPolicyViewModel { LoanDays = 14, DebtLockThreshold = 1m }));

        db.ChangeTracker.Clear();
        Assert.Equal(LoanPolicy.DefaultDebtLockThreshold, (await db.LoanPolicies.AsNoTracking().SingleAsync()).DebtLockThreshold);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    // ---------- Xác định bạn đọc được đề xuất ----------

    [Fact]
    public async Task OverdueBoundaryProposesOnlyAboveThirtyDays()
    {
        var overdue31 = await AddReaderAsync("Quá hạn 31");
        await AddLoanAsync(overdue31, dueDaysAgo: 31);
        var overdue30 = await AddReaderAsync("Quá hạn 30");
        await AddLoanAsync(overdue30, dueDaysAgo: 30);
        var notDue = await AddReaderAsync("Chưa đến hạn");
        await AddLoanAsync(notDue, dueDaysAgo: -3);

        var proposals = await GetProposalsAsync();

        var proposal = Assert.Single(proposals);
        Assert.Equal(overdue31.Id, proposal.ReaderAccountId);
        Assert.Equal(overdue31.StudentOrStaffCode, proposal.ReaderCode);
        Assert.Equal(31, proposal.MaxOverdueDays);
        Assert.True(proposal.IsOverdueViolation);
        Assert.False(proposal.IsDebtViolation);
        Assert.Equal([LockProposalViewModel.OverdueReason], proposal.Reasons);
        var loan = Assert.Single(proposal.OverdueLoans);
        Assert.Equal(31, loan.OverdueDays);
        Assert.Equal("Sách của Quá hạn 31", loan.BookTitle);
    }

    [Fact]
    public async Task ReturnedLoansAreNotCountedAsOverdue()
    {
        var reader = await AddReaderAsync("Đã trả");
        var loan = await AddLoanAsync(reader, dueDaysAgo: 45);
        loan.ReturnedAtUtc = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();

        Assert.Empty(await GetProposalsAsync());
    }

    [Fact]
    public async Task DebtIsProposedOnlyWhenGreaterThanConfiguredThreshold()
    {
        await SetThresholdAsync(100000m);
        var above = await AddReaderAsync("Nợ vượt", debt: 100001m);
        await AddReaderAsync("Nợ bằng", debt: 100000m);
        await AddReaderAsync("Nợ thấp", debt: 99999m);

        var proposal = Assert.Single(await GetProposalsAsync());

        Assert.Equal(above.Id, proposal.ReaderAccountId);
        Assert.Equal(100001m, proposal.OutstandingDebt);
        Assert.Equal(100000m, proposal.DebtThreshold);
        Assert.True(proposal.IsDebtViolation);
        Assert.False(proposal.IsOverdueViolation);
        Assert.Equal([LockProposalViewModel.DebtReason], proposal.Reasons);
    }

    [Fact]
    public async Task ReaderViolatingBothConditionsGetsBothReasons()
    {
        var reader = await AddReaderAsync("Hai vi phạm", debt: 600000m);
        await AddLoanAsync(reader, dueDaysAgo: 40);
        await AddLoanAsync(reader, dueDaysAgo: 35);
        await AddLoanAsync(reader, dueDaysAgo: 5);

        var proposal = Assert.Single(await GetProposalsAsync());

        Assert.Equal([LockProposalViewModel.OverdueReason, LockProposalViewModel.DebtReason], proposal.Reasons);
        Assert.Equal(40, proposal.MaxOverdueDays);
        Assert.Equal([40, 35], proposal.OverdueLoans.Select(loan => loan.OverdueDays));
        Assert.Equal(600000m, proposal.OutstandingDebt);
    }

    [Fact]
    public async Task LockedCardsAreExcludedAndDetectionNeverChangesCards()
    {
        var locked = await AddReaderAsync("Đang khoá", debt: 900000m);
        locked.LibraryCard!.Status = "Đang bị khoá";
        locked.LibraryCard.LockReason = "Vi phạm cũ";
        locked.LibraryCard.LockedAtUtc = DateTime.UtcNow.AddDays(-1);
        locked.LibraryCard.IsLockUntilManualUnlock = true;
        var active = await AddReaderAsync("Đang hoạt động", debt: 900000m);
        await db.SaveChangesAsync();

        var proposal = Assert.Single(await GetProposalsAsync());
        await GetProposalsAsync();

        Assert.Equal(active.Id, proposal.ReaderAccountId);
        db.ChangeTracker.Clear();
        var activeCard = await db.LibraryCards.AsNoTracking().SingleAsync(card => card.ReaderAccountId == active.Id);
        Assert.Equal("Đang hoạt động", activeCard.Status);
        Assert.Null(activeCard.LockedAtUtc);
        var lockedCard = await db.LibraryCards.AsNoTracking().SingleAsync(card => card.ReaderAccountId == locked.Id);
        Assert.Equal("Vi phạm cũ", lockedCard.LockReason);
    }

    [Fact]
    public async Task EmptyDataReturnsEmptyListAndReaderWithoutCardIsSkipped()
    {
        Assert.Empty(await GetProposalsAsync());

        var noCard = await AddReaderAsync("Không thẻ", debt: 900000m, withCard: false);
        await AddLoanAsync(noCard, dueDaysAgo: 60);

        Assert.Empty(await GetProposalsAsync());
    }

    // ---------- Xác nhận khoá từ danh sách ----------

    [Fact]
    public async Task LockingFromProposalListReusesSlice1LockAndKeepsReturnsAllowed()
    {
        var reader = await AddReaderAsync("Khoá từ đề xuất", debt: 700000m);
        var overdueLoan = await AddLoanAsync(reader, dueDaysAgo: 32, withCopy: true);
        var activeLoan = await AddLoanAsync(reader, dueDaysAgo: -5);
        var admin = new AdminAccount { Id = 1, Email = "manager@library.test", Role = AccountRoles.LibraryManager, IsActive = true, PasswordHash = "hash" };
        db.AdminAccounts.Add(admin);
        await db.SaveChangesAsync();
        Assert.Single(await GetProposalsAsync());

        var result = await CardController(AccountRoles.LibraryManager)
            .SetCardLocked(reader.Id, "Quá hạn trả sách trên 30 ngày; Nợ phí vượt ngưỡng", "temporary", DateTime.Now.AddDays(7), "/LoanPolicy#lock-proposals");

        Assert.Equal("/LoanPolicy#lock-proposals", Assert.IsType<LocalRedirectResult>(result).Url);
        db.ChangeTracker.Clear();
        var card = await db.LibraryCards.AsNoTracking().SingleAsync(item => item.ReaderAccountId == reader.Id);
        Assert.Equal("Đang bị khoá", card.Status);
        Assert.Equal("Quá hạn trả sách trên 30 ngày; Nợ phí vượt ngưỡng", card.LockReason);
        Assert.Equal(admin.Id, card.LockedByAdminAccountId);
        Assert.NotNull(card.LockExpiresAtUtc);
        Assert.Empty(await GetProposalsAsync());

        var loans = new BookLoanService(db, new WorkingScheduleService(db));
        var renew = await loans.RenewAsync(activeLoan.Id, today);
        Assert.False(renew.IsSuccess);
        Assert.Contains("đang bị khoá", renew.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(activeLoan.DueDate, (await db.BookLoans.AsNoTracking().SingleAsync(item => item.Id == activeLoan.Id)).DueDate);

        var returned = await loans.ReturnAsync(overdueLoan.Id, DateTime.UtcNow);
        Assert.True(returned.IsSuccess, returned.ErrorMessage);
        db.ChangeTracker.Clear();
        Assert.Equal("Đang bị khoá", (await db.LibraryCards.AsNoTracking().SingleAsync(item => item.ReaderAccountId == reader.Id)).Status);
    }

    [Fact]
    public async Task InvalidLockFromProposalListReturnsToListWithoutLocking()
    {
        var reader = await AddReaderAsync("Lý do trống", debt: 700000m);
        db.AdminAccounts.Add(new AdminAccount { Id = 1, Email = "manager@library.test", Role = AccountRoles.LibraryManager, IsActive = true, PasswordHash = "hash" });
        await db.SaveChangesAsync();

        var controller = CardController(AccountRoles.LibraryManager);
        var result = await controller.SetCardLocked(reader.Id, "   ", "temporary", null, "/LoanPolicy#lock-proposals");

        Assert.IsType<LocalRedirectResult>(result);
        Assert.Contains("lý do", controller.TempData["ErrorMessage"] as string, StringComparison.OrdinalIgnoreCase);
        db.ChangeTracker.Clear();
        Assert.Null((await db.LibraryCards.AsNoTracking().SingleAsync()).LockedAtUtc);
        Assert.Single(await GetProposalsAsync());
    }

    [Fact]
    public async Task ExternalReturnUrlIsIgnored()
    {
        var reader = await AddReaderAsync("Chuyển hướng", debt: 700000m);
        db.AdminAccounts.Add(new AdminAccount { Id = 1, Email = "manager@library.test", Role = AccountRoles.LibraryManager, IsActive = true, PasswordHash = "hash" });
        await db.SaveChangesAsync();

        var result = await CardController(AccountRoles.LibraryManager)
            .SetCardLocked(reader.Id, "Vi phạm", "manual", null, "//evil.example/");

        Assert.IsType<RedirectToActionResult>(result);
    }

    // ---------- Danh sách cập nhật theo dữ liệu mới ----------

    [Fact]
    public async Task ProposalDisappearsAfterReturnOrWhenDebtDropsToThreshold()
    {
        var overdueReader = await AddReaderAsync("Sẽ trả sách");
        var loan = await AddLoanAsync(overdueReader, dueDaysAgo: 31, withCopy: true);
        var debtReader = await AddReaderAsync("Sẽ giảm nợ", debt: 500001m);
        Assert.Equal(2, (await GetProposalsAsync()).Count);

        Assert.True((await new BookLoanService(db, new WorkingScheduleService(db)).ReturnAsync(loan.Id, DateTime.UtcNow)).IsSuccess);
        var stored = await db.ReaderAccounts.SingleAsync(item => item.Id == debtReader.Id);
        stored.OutstandingBalance = 500000m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Empty(await GetProposalsAsync());
    }

    // ---------- Hỗ trợ ----------

    private async Task<List<LockProposalViewModel>> GetProposalsAsync()
    {
        var result = Assert.IsType<OkObjectResult>(await PolicyController(AccountRoles.LibraryManager).GetLockProposalsApi());
        return Assert.IsType<List<LockProposalViewModel>>(result.Value);
    }

    private async Task SetThresholdAsync(decimal threshold)
    {
        var policy = await db.LoanPolicies.SingleAsync();
        policy.DebtLockThreshold = threshold;
        await db.SaveChangesAsync();
    }

    private async Task<ReaderAccount> AddReaderAsync(string name, decimal debt = 0m, bool withCard = true)
    {
        var index = ++sequence;
        var reader = new ReaderAccount
        {
            FullName = name, DateOfBirth = new DateOnly(2000, 1, 1), Email = $"proposal{index}@example.test",
            PhoneNumber = $"09000000{index:00}", StudentOrStaffCode = $"BD-DX-{index:00}", PasswordHash = "hash",
            Status = "Đang hoạt động", EmailConfirmed = true, OutstandingBalance = debt
        };
        if (withCard)
        {
            reader.LibraryCard = new LibraryCard
            {
                CardCode = $"CARD-DX-{index:00}",
                LibraryCardType = new LibraryCardType { Name = $"Loại thẻ đề xuất {index}", MaxRenewals = 2 },
                IssuedOn = today.AddYears(-1), ExpiresOn = today.AddYears(1), Status = "Đang hoạt động"
            };
        }
        db.ReaderAccounts.Add(reader);
        await db.SaveChangesAsync();
        return reader;
    }

    private async Task<BookLoan> AddLoanAsync(ReaderAccount reader, int dueDaysAgo, bool withCopy = false)
    {
        var index = ++sequence;
        var book = new Book { Title = $"Sách của {reader.FullName}", Author = new Author { Name = $"Tác giả {index}" } };
        BookCopy? copy = null;
        if (withCopy)
        {
            var shelf = new Shelf { Code = $"KE-{index}", Name = $"Kệ {index}", Warehouse = new Warehouse { Code = $"KHO-{index}", Name = $"Kho {index}" } };
            copy = new BookCopy { Book = book, Shelf = shelf, CopyCode = $"BS-DX-{index}", Status = BookCopyStatus.OnLoan };
            db.BookCopies.Add(copy);
        }
        var dueDate = today.AddDays(-dueDaysAgo);
        var loan = new BookLoan
        {
            Book = book, BookCopy = copy, ReaderAccountId = reader.Id,
            LoanDate = dueDate.AddDays(-14), OriginalDueDate = dueDate, DueDate = dueDate
        };
        db.BookLoans.Add(loan);
        await db.SaveChangesAsync();
        return loan;
    }

    private LoanPolicyController PolicyController(string? role)
    {
        var context = NewHttpContext();
        var controller = new LoanPolicyController(db, new FixedStaffAuditLogService(new AuditLogService(db, NullLogger<AuditLogService>.Instance), role))
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            Url = new FixedUrlHelper()
        };
        controller.TempData = new TempDataDictionary(context, new NoTempDataProvider());
        return controller;
    }

    private ReaderAccountController CardController(string? role)
    {
        var context = NewHttpContext();
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

    private static DefaultHttpContext NewHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        return context;
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
            Task.FromResult(role is null ? null : new AdminAccount { Id = 1, Email = "manager@library.test", Role = role, IsActive = true });
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
