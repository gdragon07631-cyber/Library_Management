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

public sealed partial class DirectLoanTests : IDisposable
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

    [Theory]
    [InlineData("missing", "Mã vạch không tồn tại trong hệ thống.")]
    [InlineData(BookCopyStatus.OnLoan, "Bản sao này đang được mượn, không thể tiếp tục cho mượn.")]
    [InlineData(BookCopyStatus.UnderRepair, "Bản sao đang sửa chữa, chưa thể cho mượn.")]
    [InlineData(BookCopyStatus.Removed, "Bản sao đã bị loại khỏi kho, không thể cho mượn.")]
    [InlineData(BookCopyStatus.OnHold, "Bản sao đang được giữ cho đặt trước, chưa đủ điều kiện cho mượn.")]
    public async Task RowValidationReturnsExactErrorAndPreservesOtherRows(string status, string message)
    {
        await AddCopies(2);
        if (status != "missing")
        {
            (await db.BookCopies.SingleAsync(c => c.CopyCode == "COPY02")).Status = status;
            await db.SaveChangesAsync();
        }
        var codes = new[] { "COPY01", status == "missing" ? "MISSING" : "COPY02" };
        var rows = await service.ValidateRowsAsync(codes, ["good-row", "bad-row"]);
        Assert.Equal("good-row", rows[0].RowId);
        Assert.True(rows[0].IsValid);
        Assert.Equal("bad-row", rows[1].RowId);
        Assert.Equal(message, rows[1].Error);
        Assert.Empty(await db.BookLoans.ToListAsync());
        var result = await service.ConfirmManyAsync("CARD01", codes, staff, rowIds: ["good-row", "bad-row"]);
        Assert.False(result.IsSuccess);
        Assert.Equal(message, result.Rows![1].Error);
        Assert.True(result.Rows[0].IsValid);
        Assert.Empty(await db.BookLoans.ToListAsync());
        Assert.Equal(BookCopyStatus.Available, (await db.BookCopies.SingleAsync(c => c.CopyCode == "COPY01")).Status);
    }

    [Fact]
    public async Task FiveMixedRowsCanBeCorrectedWithoutLosingIdsOrSavingPartialLoans()
    {
        await AddCopies(5);
        (await db.BookCopies.SingleAsync(c => c.CopyCode == "COPY04")).Status = BookCopyStatus.OnLoan;
        await db.SaveChangesAsync();
        string[] codes = ["COPY01", "UNKNOWN", "COPY02", "COPY04", "COPY03"];
        string[] ids = ["row1", "row2", "row3", "row4", "row5"];
        var failed = await service.ConfirmManyAsync("CARD01", codes, staff, rowIds: ids);
        Assert.False(failed.IsSuccess);
        var failedRows = Assert.IsAssignableFrom<IReadOnlyList<DirectLoanSelection>>(failed.Rows);
        Assert.Equal(3, failedRows.Count(r => r.IsValid));
        Assert.Equal(2, failedRows.Count(r => !r.IsValid));
        Assert.Equal(ids, failedRows.Select(r => r.RowId));
        codes[1] = "COPY01";
        var duplicate = await service.ValidateRowsAsync(codes, ids);
        Assert.Contains("đã có trong danh sách", duplicate[1].Error!);
        codes[1] = "COPY05";
        var corrected = await service.ValidateRowsAsync(codes, ids);
        Assert.True(corrected[1].IsValid);
        Assert.Equal("row2", corrected[1].RowId);
        var success = await service.ConfirmManyAsync("CARD01", [codes[0], codes[1], codes[2], codes[4]], staff,
            rowIds: [ids[0], ids[1], ids[2], ids[4]]);
        Assert.True(success.IsSuccess, success.ErrorMessage);
        Assert.Equal(4, await db.BookLoans.CountAsync());
    }

    [Fact]
    public async Task ChangedCopyIsReturnedWithOriginalRowId()
    {
        var codes = await AddCopies(3);
        string[] ids = ["first", "second", "third"];
        Assert.All(await service.ValidateRowsAsync(codes, ids), r => Assert.True(r.IsValid));
        (await db.BookCopies.SingleAsync(c => c.CopyCode == "COPY02")).Status = BookCopyStatus.UnderRepair;
        await db.SaveChangesAsync();
        var result = await service.ConfirmManyAsync("CARD01", codes, staff, rowIds: ids);
        Assert.False(result.IsSuccess);
        Assert.Equal("second", Assert.Single(result.Rows!, r => !r.IsValid).RowId);
        Assert.Empty(await db.BookLoans.ToListAsync());
        Assert.Empty(await db.BookCopyStatusHistories.ToListAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("same")]
    public async Task InvalidRowIdsCannotBeConfirmed(string secondId)
    {
        var codes = await AddCopies(2);
        Assert.False((await service.ConfirmManyAsync("CARD01", codes, staff, rowIds: ["same", secondId])).IsSuccess);
        Assert.Empty(await db.BookLoans.ToListAsync());
    }

    private async Task<string[]> AddCopies(int count)
    {
        var first = await db.BookCopies.FirstAsync();
        for (var i = 2; i <= count; i++)
            db.BookCopies.Add(new BookCopy { CopyCode = $"COPY{i:00}", BookId = first.BookId, ShelfId = first.ShelfId });
        await db.SaveChangesAsync();
        return Enumerable.Range(1, count).Select(i => $"COPY{i:00}").ToArray();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task BatchCreatesEveryLoanAndUpdatesCountsTogether(int count)
    {
        var codes = await AddCopies(count);
        var result = await service.ConfirmManyAsync("CARD01", codes, staff);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(count, result.Loans!.Count);
        Assert.Equal(count, await db.BookLoans.CountAsync());
        Assert.Equal(count, await db.BookCopies.CountAsync(c => c.Status == BookCopyStatus.OnLoan));
        Assert.Equal(count, await db.BookCopyStatusHistories.CountAsync());
        Assert.Equal(count, await db.AuditLogs.CountAsync());
        Assert.All(result.Loans, loan => { Assert.NotNull(loan.BookCopyId); Assert.Equal(today.AddDays(7), loan.DueDate); });
        var page = await service.LookupAsync("CARD01", null);
        Assert.Equal(count, page.CurrentLoans);
        Assert.Equal(5 - count, page.RemainingLoans);
        Assert.False((await service.ConfirmManyAsync("CARD01", codes, staff)).IsSuccess);
        Assert.Equal(count, await db.BookLoans.CountAsync());
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("changed")]
    [InlineData("limit")]
    [InlineData("locked")]
    public async Task InvalidBatchLeavesAllAvailableCopiesUnchanged(string scenario)
    {
        var codes = await AddCopies(3);
        var card = await db.LibraryCards.Include(c => c.ReaderAccount).SingleAsync();
        switch (scenario)
        {
            case "empty": codes = []; break;
            case "duplicate": codes = ["COPY01", " COPY01 "]; break;
            case "missing": codes[2] = "MISSING"; break;
            case "changed": (await db.BookCopies.SingleAsync(c => c.CopyCode == "COPY03")).Status = BookCopyStatus.OnHold; break;
            case "limit": AddLoans(card.ReaderAccountId, 3, today.AddDays(3)); break;
            case "locked": card.ReaderAccount!.IsLocked = true; break;
        }
        await db.SaveChangesAsync();
        var before = await db.BookLoans.CountAsync();
        Assert.False((await service.ConfirmManyAsync("CARD01", codes, staff)).IsSuccess);
        Assert.Equal(before, await db.BookLoans.CountAsync());
        Assert.Equal(0, await db.BookCopies.CountAsync(c => c.Status == BookCopyStatus.OnLoan));
        Assert.Empty(await db.BookCopyStatusHistories.ToListAsync());
    }

    [Fact]
    public async Task StaffCardCanBorrowTenWithoutSeparateBatchLimit()
    {
        (await db.LibraryCards.Include(c => c.LibraryCardType).SingleAsync()).LibraryCardType!.Name = "Thẻ cán bộ";
        var codes = await AddCopies(10);
        var result = await service.ConfirmManyAsync("CARD01", codes, staff);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(10, await db.BookLoans.CountAsync());
        Assert.Equal(0, (await service.LookupAsync("CARD01", null)).RemainingLoans);
    }

    [Fact]
    public async Task FailureAfterFirstCompleteLineRollsBackEntireBatch()
    {
        var codes = await AddCopies(3);
        failure.FailAt = 4;
        failure.Enabled = true;
        Assert.False((await service.ConfirmManyAsync("CARD01", codes, staff)).IsSuccess);
        Assert.Equal(4, failure.Saves);
        Assert.Empty(await db.BookLoans.ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
        Assert.Empty(await db.BookCopyStatusHistories.ToListAsync());
        Assert.Equal(3, await db.BookCopies.CountAsync(c => c.Status == BookCopyStatus.Available));
        failure.Enabled = false;
        await db.SaveChangesAsync();
        Assert.Empty(await db.BookLoans.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentBatchesCannotPartiallySaveOrExceedQuota(bool overlap)
    {
        await AddCopies(6);
        using var barrier = new Barrier(2);
        async Task<DirectLoanBatchOutcome> Run(string[] codes) => await Task.Run(async () =>
        {
            using var context = new ApplicationDbContext(Options().Options);
            var schedule = new WorkingScheduleService(context);
            var direct = new DirectLoanService(context, new BookLoanService(context, schedule), schedule, NullLogger<DirectLoanService>.Instance);
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            return await direct.ConfirmManyAsync("CARD01", codes, staff);
        });
        var results = await Task.WhenAll(Run(["COPY01", "COPY02", "COPY03"]),
            Run(overlap ? ["COPY03", "COPY04", "COPY05"] : ["COPY04", "COPY05", "COPY06"]));
        Assert.Single(results, r => r.IsSuccess);
        Assert.Equal(3, await db.BookLoans.CountAsync());
        Assert.Equal(3, await db.BookCopies.CountAsync(c => c.Status == BookCopyStatus.OnLoan));
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StaffHttpFlowRendersLookupAndReceiptEnforcesAntiforgeryAndRejectsAnonymous(bool batch, bool ownHold)
    {
        if (batch) await AddCopies(3);
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
            var fields = new Dictionary<string, string> { ["CardCode"] = "CARD01", ["CopyCode"] = "COPY01", ["RequestId"] = Guid.NewGuid().ToString() };
            var endpoint = batch ? "/DirectLoan/ConfirmMany" : "/DirectLoan/Confirm";
            if (batch)
            {
                fields.Remove("CopyCode");
                for (var i = 0; i < 3; i++) fields[$"CopyCodes[{i}]"] = $"COPY{i + 1:00}";
                for (var i = 0; i < 3; i++) fields[$"RowIds[{i}]"] = $"http-row-{i}";
                using var lookup = await client.GetAsync("/DirectLoan/LookupCopy?cardCode=CARD01&copyCode=COPY02");
                lookup.EnsureSuccessStatusCode();
                Assert.Contains("COPY02", await lookup.Content.ReadAsStringAsync());
                Assert.Empty(await db.BookLoans.ToListAsync());
                using var invalidLookup = await client.GetAsync("/DirectLoan/LookupCopy?cardCode=CARD01&copyCode=MISSING&rowId=missing-row");
                invalidLookup.EnsureSuccessStatusCode();
                var invalidRow = System.Text.Json.JsonDocument.Parse(await invalidLookup.Content.ReadAsStringAsync()).RootElement;
                Assert.Equal("missing-row", invalidRow.GetProperty("rowId").GetString());
                Assert.False(invalidRow.GetProperty("isValid").GetBoolean());
                Assert.Equal("Mã vạch không tồn tại trong hệ thống.", invalidRow.GetProperty("error").GetString());
                var duplicateFields = new Dictionary<string, string>(fields)
                {
                    ["CopyCodes[1]"] = "COPY01", ["__RequestVerificationToken"] = antiforgery, ["RequestId"] = Guid.NewGuid().ToString()
                };
                using var duplicate = await client.PostAsync(endpoint, new FormUrlEncodedContent(duplicateFields));
                var duplicateHtml = WebUtility.HtmlDecode(await duplicate.Content.ReadAsStringAsync());
                Assert.Contains("Mã vạch này đã có trong danh sách mượn.", duplicateHtml);
                Assert.Contains("data-code=\"COPY03\"", duplicateHtml);
                Assert.Empty(await db.BookLoans.ToListAsync());
                await db.BookCopies.Where(c => c.CopyCode == "COPY02")
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, BookCopyStatus.UnderRepair));
                var changedFields = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = antiforgery, ["RequestId"] = Guid.NewGuid().ToString() };
                using var changedResponse = await client.PostAsync(endpoint, new FormUrlEncodedContent(changedFields));
                var changedHtml = WebUtility.HtmlDecode(await changedResponse.Content.ReadAsStringAsync());
                var secondRow = Regex.Match(changedHtml, "<tr[^>]*data-row-id=\"http-row-1\"[^>]*>[\\s\\S]*?</tr>").Value;
                Assert.Contains("Bản sao đang sửa chữa, chưa thể cho mượn.", secondRow);
                Assert.Contains("data-row-id=\"http-row-0\"", changedHtml);
                Assert.Contains("data-row-id=\"http-row-2\"", changedHtml);
                Assert.Empty(await db.BookLoans.ToListAsync());
                Assert.Equal(2, await db.BookCopies.CountAsync(c => c.Status == BookCopyStatus.Available));
                await db.BookCopies.Where(c => c.CopyCode == "COPY02")
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, BookCopyStatus.Available));
            }
            using var invalidCsrf = await client.PostAsync(endpoint, new FormUrlEncodedContent(fields));
            Assert.Equal(HttpStatusCode.BadRequest, invalidCsrf.StatusCode);
            Assert.Empty(await db.BookLoans.ToListAsync());
            if (ownHold)
            {
                var foreign = await AddHoldAsync("COPY02", other: true);
                using var holdLookup = await client.GetAsync("/DirectLoan/LookupCopy?cardCode=CARD01&copyCode=COPY02&rowId=hold-row");
                var holdJson = await holdLookup.Content.ReadAsStringAsync();
                var holdRow = System.Text.Json.JsonDocument.Parse(holdJson).RootElement;
                Assert.Equal(foreign.Id, holdRow.GetProperty("holdId").GetInt64());
                Assert.Equal("hold-row", holdRow.GetProperty("rowId").GetString());
                Assert.False(holdRow.GetProperty("isValid").GetBoolean());
                Assert.DoesNotContain("private@test.local", holdJson);
                Assert.DoesNotContain("0998765432", holdJson);
                Assert.False(holdRow.TryGetProperty("readerAccount", out _));
                var rejectedFields = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = antiforgery, ["RequestId"] = Guid.NewGuid().ToString() };
                using var rejected = await client.PostAsync(endpoint, new FormUrlEncodedContent(rejectedFields));
                var rejectedHtml = WebUtility.HtmlDecode(await rejected.Content.ReadAsStringAsync());
                var heldHtml = Regex.Match(rejectedHtml, "<tr[^>]*data-row-id=\"http-row-1\"[^>]*>[\\s\\S]*?</tr>").Value;
                Assert.Contains($"#{foreign.Id}", heldHtml);
                Assert.Contains("của bạn đọc khác", heldHtml);
                Assert.DoesNotContain("Tên người giữ riêng tư", rejectedHtml);
                Assert.Empty(await db.BookLoans.ToListAsync());
                foreign.ReaderAccountId = (await db.LibraryCards.SingleAsync(c => c.CardCode == "CARD01")).ReaderAccountId;
                await db.SaveChangesAsync();
                using var ownerLookup = await client.GetAsync("/DirectLoan/LookupCopy?cardCode=CARD01&copyCode=COPY02&rowId=hold-row");
                var ownerRow = System.Text.Json.JsonDocument.Parse(await ownerLookup.Content.ReadAsStringAsync()).RootElement;
                Assert.True(ownerRow.GetProperty("isValid").GetBoolean());
            }
            fields["__RequestVerificationToken"] = antiforgery;
            using var response = await client.PostAsync(endpoint, new FormUrlEncodedContent(fields));
            response.EnsureSuccessStatusCode();
            var receipt = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
            Assert.Contains("Xác nhận mượn sách thành công.", receipt);
            Assert.Contains("Phiếu mượn #", receipt);
            Assert.Contains("Đang mượn", receipt);
            Assert.Contains($"id=\"remainingLoans\">{(batch ? 2 : 4)}", receipt);
            Assert.Equal(batch ? 3 : 1, await db.BookLoans.CountAsync());
            if (ownHold)
            {
                Assert.Contains("Đơn đặt trước", receipt);
                Assert.Equal(BookHoldStatus.ConvertedToLoan, (await db.BookHolds.AsNoTracking().SingleAsync()).Status);
                Assert.Single(await db.BookLoans.Where(l => l.SourceBookHoldId != null).ToListAsync());
            }
            if (batch)
            {
                Assert.Contains("COPY02", receipt);
                Assert.Contains("COPY03", receipt);
                var refreshed = await client.GetStringAsync(response.RequestMessage!.RequestUri);
                Assert.Contains("COPY03", refreshed);
            }
            using var repeat = await client.PostAsync(endpoint, new FormUrlEncodedContent(fields));
            var repeatHtml = WebUtility.HtmlDecode(await repeat.Content.ReadAsStringAsync());
            Assert.Contains("Xác nhận mượn sách thành công.", repeatHtml);
            Assert.Equal(batch ? 3 : 1, await db.BookLoans.CountAsync());
        }
        finally { await app.StopAsync(); }
    }

    private sealed class FailSecondSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public int Saves { get; private set; }
        public int FailAt { get; set; } = 2;
        public bool WrappedFailure { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && ++Saves == FailAt)
            {
                if (WrappedFailure) throw new InvalidOperationException("Simulated EF wrapped database failure", new SqliteException("Simulated lock failure", 5));
                throw new DbUpdateException("Simulated final save failure");
            }
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
