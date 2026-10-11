using Microsoft.Data.Sqlite;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class DirectLoanTests : IDisposable
{
    private readonly SqliteConnection connection = new($"Data Source=DirectLoanTest_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5");
    private readonly string? sqlConnection = Environment.GetEnvironmentVariable("DIRECT_LOAN_SQLSERVER_TEST") == "1"
        ? $"Server=(localdb)\\MSSQLLocalDB;Database=DirectLoanTest_{Guid.NewGuid():N};Integrated Security=true;TrustServerCertificate=true" : null;
    private readonly FailSecondSave failure = new();
    private readonly ApplicationDbContext db;
    private readonly DirectLoanService service;
    private readonly AdminAccount staff;
    private readonly DateOnly today = DateOnly.FromDateTime(DateTime.Today);

    public DirectLoanTests()
    {
        connection.Open();
        db = new(Options().AddInterceptors(failure).Options);
        db.Database.EnsureCreated();
        staff = new AdminAccount { Email = "staff@test.local", PasswordHash = "test", Role = AccountRoles.Librarian };
        var reader = new ReaderAccount { FullName = "Bạn đọc thử nghiệm", Email = "reader@test.local", PasswordHash = "test", Status = "Đang hoạt động", StudentOrStaffCode = "SV01", PhoneNumber = "0912345678" };
        var type = new LibraryCardType { Name = "Thẻ sinh viên", LoanDays = 7 };
        db.LibraryCards.Add(new LibraryCard { CardCode = "CARD01", ReaderAccount = reader, LibraryCardType = type, IssuedOn = today.AddDays(-30), ExpiresOn = today.AddYears(1) });
        var shelf = new Shelf { Code = "S1", Name = "Kệ thử nghiệm", Warehouse = new Warehouse { Code = "W1", Name = "Kho thử nghiệm" } };
        var book = new Book { Title = "Sách thử nghiệm", Author = new Author { Name = "Tác giả thử nghiệm" } };
        db.BookCopies.Add(new BookCopy { CopyCode = "COPY01", Book = book, Shelf = shelf });
        db.AdminAccounts.Add(staff);
        db.SaveChanges();
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            var schedule = db.WeeklyWorkingSchedules.SingleOrDefault(s => s.DayOfWeek == day);
            if (schedule == null) db.WeeklyWorkingSchedules.Add(new WeeklyWorkingSchedule { DayOfWeek = day, IsOpen = true });
            else schedule.IsOpen = true;
        }
        db.SaveChanges();
        var schedules = new WorkingScheduleService(db);
        service = new(db, new BookLoanService(db, schedules), schedules, NullLogger<DirectLoanService>.Instance);
    }

    [Fact]
    public async Task LookupDisplaysReaderAndCopyWithoutWriting()
    {
        var result = await service.LookupAsync(" CARD01 ", " COPY01 ");
        Assert.True(result.CanConfirm);
        Assert.Equal("Bạn đọc thử nghiệm", result.ReaderName);
        Assert.Equal("Thẻ sinh viên", result.CardTypeName);
        Assert.Equal(0, result.CurrentLoans);
        Assert.Equal(5, result.RemainingLoans);
        Assert.Equal("Sách thử nghiệm", result.BookTitle);
        Assert.Equal(BookCopyStatus.Available, result.CopyStatus);
        Assert.Empty(await db.BookLoans.ToListAsync());
        Assert.Empty(await db.BookCopyStatusHistories.ToListAsync());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("missing-card")]
    [InlineData("locked")]
    [InlineData("expired")]
    [InlineData("not-issued")]
    [InlineData("inactive-type")]
    [InlineData("no-days")]
    [InlineData("pending")]
    [InlineData("debt")]
    [InlineData("overdue")]
    [InlineData("limit")]
    public async Task IneligibleReadersCannotConfirm(string scenario)
    {
        var card = await db.LibraryCards.Include(c => c.ReaderAccount).Include(c => c.LibraryCardType).SingleAsync();
        switch (scenario)
        {
            case "locked": card.ReaderAccount!.IsLocked = true; break;
            case "expired": card.ExpiresOn = today.AddDays(-1); break;
            case "not-issued": card.IssuedOn = today.AddDays(1); break;
            case "inactive-type": card.LibraryCardType!.IsActive = false; break;
            case "no-days": card.LibraryCardType!.LoanDays = null; break;
            case "pending": card.ReaderAccount!.Status = "Chờ duyệt"; break;
            case "debt": card.ReaderAccount!.OutstandingBalance = 1000; break;
            case "overdue": AddLoans(card.ReaderAccountId, 1, today.AddDays(-1)); break;
            case "limit": AddLoans(card.ReaderAccountId, 5, today.AddDays(5)); break;
        }
        await db.SaveChangesAsync();
        var before = await db.BookLoans.CountAsync();
        var code = scenario == "missing-card" ? "UNKNOWN" : "CARD01";
        var lookup = await service.LookupAsync(code, "COPY01");
        Assert.False(lookup.CanConfirm);
        Assert.NotNull(lookup.ReaderError);
        var result = await service.ConfirmAsync(code, "COPY01", staff);
        Assert.False(result.IsSuccess);
        Assert.Equal(before, await db.BookLoans.CountAsync());
        Assert.Equal(BookCopyStatus.Available, (await db.BookCopies.SingleAsync()).Status);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData(BookCopyStatus.OnLoan)]
    [InlineData(BookCopyStatus.OnHold)]
    [InlineData(BookCopyStatus.UnderRepair)]
    [InlineData(BookCopyStatus.Removed)]
    public async Task MissingOrUnavailableCopyCannotBeIssued(string status)
    {
        if (status != "missing") { (await db.BookCopies.SingleAsync()).Status = status; await db.SaveChangesAsync(); }
        var code = status == "missing" ? "UNKNOWN" : "COPY01";
        Assert.False((await service.LookupAsync("CARD01", code)).CanConfirm);
        Assert.False((await service.ConfirmAsync("CARD01", code, staff)).IsSuccess);
        Assert.Empty(await db.BookLoans.ToListAsync());
        Assert.Empty(await db.BookCopyStatusHistories.ToListAsync());
    }

    [Fact]
    public async Task SuccessPersistsCopyLinkDatesHistoryAndUpdatesCountsAndRejectsRepeat()
    {
        db.HolidayClosures.Add(new HolidayClosure { HolidayDate = today.AddDays(7), Reason = "Nghỉ thử nghiệm" });
        await db.SaveChangesAsync();
        var result = await service.ConfirmAsync(" CARD01 ", " COPY01 ", staff);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        var loan = await db.BookLoans.SingleAsync();
        var copy = await db.BookCopies.SingleAsync();
        Assert.Equal(copy.Id, loan.BookCopyId);
        Assert.Equal(copy.BookId, loan.BookId);
        Assert.Equal((await db.LibraryCards.SingleAsync()).ReaderAccountId, loan.ReaderAccountId);
        Assert.Equal(staff.Id, loan.CreatedByAdminAccountId);
        Assert.Null(loan.SourceBookHoldId);
        Assert.Equal(today, loan.LoanDate);
        Assert.Equal(today.AddDays(7), loan.OriginalDueDate);
        Assert.Equal(today.AddDays(8), loan.DueDate);
        Assert.Equal(BookCopyStatus.OnLoan, copy.Status);
        Assert.Single(await db.BookCopyStatusHistories.ToListAsync());
        Assert.Single(await db.AuditLogs.ToListAsync());
        var page = await service.LookupAsync("CARD01", "COPY01");
        Assert.Equal(1, page.CurrentLoans);
        Assert.Equal(4, page.RemainingLoans);
        Assert.NotNull(await service.ReceiptAsync(loan.Id));
        Assert.False((await service.ConfirmAsync("CARD01", "COPY01", staff)).IsSuccess);
        Assert.Single(await db.BookLoans.ToListAsync());
        var schedules = new WorkingScheduleService(db);
        Assert.Contains(await new BookLoanService(db, schedules).GetAllAsync(), l => l.Id == loan.Id);
    }

    [Fact]
    public async Task SaveFailureAfterLoanInsertRollsBackLoanCopyAuditAndHistory()
    {
        failure.Enabled = true;
        var result = await service.ConfirmAsync("CARD01", "COPY01", staff);
        Assert.False(result.IsSuccess);
        Assert.Equal(2, failure.Saves);
        Assert.Empty(await db.BookLoans.ToListAsync());
        Assert.Empty(await db.BookCopyStatusHistories.ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
        Assert.Equal(BookCopyStatus.Available, (await db.BookCopies.SingleAsync()).Status);
        failure.Enabled = false;
        await db.SaveChangesAsync();
        Assert.Empty(await db.BookLoans.ToListAsync());
    }

    [Fact]
    public async Task ConfirmationRechecksStateAfterLookup()
    {
        Assert.True((await service.LookupAsync("CARD01", "COPY01")).CanConfirm);
        (await db.BookCopies.SingleAsync()).Status = BookCopyStatus.OnHold;
        await db.SaveChangesAsync();
        Assert.False((await service.ConfirmAsync("CARD01", "COPY01", staff)).IsSuccess);
        Assert.Empty(await db.BookLoans.ToListAsync());
    }

    private void AddLoans(int readerId, int count, DateOnly dueDate)
    {
        var bookId = db.Books.Single().Id;
        for (var i = 0; i < count; i++) db.BookLoans.Add(new BookLoan { BookId = bookId, ReaderAccountId = readerId, LoanDate = today.AddDays(-7), OriginalDueDate = dueDate, DueDate = dueDate });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentRequestsCannotIssueSameCopyOrExceedReaderLimit(bool differentCopies)
    {
        if (differentCopies)
        {
            var copy = await db.BookCopies.SingleAsync();
            db.BookCopies.Add(new BookCopy { CopyCode = "COPY02", BookId = copy.BookId, ShelfId = copy.ShelfId });
            AddLoans((await db.LibraryCards.SingleAsync()).ReaderAccountId, 4, today.AddDays(3));
            await db.SaveChangesAsync();
        }
        using var barrier = new Barrier(2);
        async Task<BookLoanOutcome> Run(string code) => await Task.Run(async () =>
        {
            using var context = new ApplicationDbContext(Options().Options);
            var schedule = new WorkingScheduleService(context);
            var direct = new DirectLoanService(context, new BookLoanService(context, schedule), schedule, NullLogger<DirectLoanService>.Instance);
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            return await direct.ConfirmAsync("CARD01", code, staff);
        });
        var results = await Task.WhenAll(Run("COPY01"), Run(differentCopies ? "COPY02" : "COPY01"));
        Assert.Single(results, r => r.IsSuccess);
        db.ChangeTracker.Clear();
        Assert.Equal(differentCopies ? 5 : 1, await db.BookLoans.CountAsync());
        Assert.Equal(1, await db.BookCopies.CountAsync(c => c.Status == BookCopyStatus.OnLoan));
    }

    private DbContextOptionsBuilder<ApplicationDbContext> Options()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>();
        return sqlConnection == null ? options.UseSqlite(connection.ConnectionString) : options.UseSqlServer(sqlConnection);
    }

    [Fact]
    public async Task StaffHttpFlowRendersLookupAndReceiptEnforcesAntiforgeryAndRejectsAnonymous()
    {
        const string token = "direct-loan-test-session";
        db.RefreshTokens.Add(new RefreshToken { AdminAccountId = staff.Id, TokenHash = TokenService.HashRefreshToken(token), CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Project.Controllers.DirectLoanController).Assembly.GetName().Name,
            EnvironmentName = "Development"
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllersWithViews();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddScoped(_ => new ApplicationDbContext(Options().Options));
        builder.Services.AddScoped<DirectLoanService>();
        builder.Services.AddScoped<IBookLoanService, BookLoanService>();
        builder.Services.AddScoped<IWorkingScheduleService, WorkingScheduleService>();
        builder.Services.AddScoped<IAuditLogService, AuditLogService>();
        await using var app = builder.Build();
        app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var uri = new Uri(address);
            using var anonymous = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = uri };
            using var denied = await anonymous.GetAsync("/DirectLoan");
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
            Assert.Contains("Account/Login", denied.Headers.Location!.ToString());
            var cookies = new CookieContainer();
            cookies.Add(uri, new Cookie("admin_refresh", token));
            using var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies, UseProxy = false }) { BaseAddress = uri };
            var html = await client.GetStringAsync("/DirectLoan?cardCode=CARD01&copyCode=COPY01");
            var decoded = WebUtility.HtmlDecode(html);
            Assert.Contains("Bạn đọc thử nghiệm", decoded);
            Assert.Contains("Sách thử nghiệm", decoded);
            Assert.Contains("Xác nhận mượn", decoded);
            var antiforgeryInput = Regex.Match(html, "<input[^>]*name=\"__RequestVerificationToken\"[^>]*>").Value;
            var antiforgery = WebUtility.HtmlDecode(Regex.Match(antiforgeryInput, "value=\"([^\"]+)\"").Groups[1].Value);
            Assert.NotEmpty(antiforgery);
            var fields = new Dictionary<string, string> { ["CardCode"] = "CARD01", ["CopyCode"] = "COPY01" };
            using var invalidCsrf = await client.PostAsync("/DirectLoan/Confirm", new FormUrlEncodedContent(fields));
            Assert.Equal(HttpStatusCode.BadRequest, invalidCsrf.StatusCode);
            Assert.Empty(await db.BookLoans.ToListAsync());
            fields["__RequestVerificationToken"] = antiforgery;
            using var response = await client.PostAsync("/DirectLoan/Confirm", new FormUrlEncodedContent(fields));
            response.EnsureSuccessStatusCode();
            var receipt = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
            Assert.Contains("Cho mượn thành công.", receipt);
            Assert.Contains("Phiếu mượn #", receipt);
            Assert.Contains("Đang mượn", receipt);
            Assert.Contains("id=\"remainingLoans\">4", receipt);
            Assert.Single(await db.BookLoans.ToListAsync());
            using var repeat = await client.PostAsync("/DirectLoan/Confirm", new FormUrlEncodedContent(fields));
            Assert.Contains("không thể cho mượn trực tiếp", WebUtility.HtmlDecode(await repeat.Content.ReadAsStringAsync()));
            Assert.Single(await db.BookLoans.ToListAsync());
        }
        finally { await app.StopAsync(); }
    }

    private sealed class FailSecondSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public int Saves { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && ++Saves == 2) throw new DbUpdateException("Simulated final save failure");
            return ValueTask.FromResult(result);
        }
    }
    public void Dispose()
    {
        // Only the unique DirectLoanTest_<GUID> database created by this fixture is removed.
        if (sqlConnection != null) db.Database.EnsureDeleted();
        db.Dispose(); connection.Dispose();
    }
}
