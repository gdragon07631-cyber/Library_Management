using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Project.Controllers;
using Project.Data;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class OverdueReaderExportTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;
    private readonly RejectAudit interceptor = new();
    private readonly WorkingScheduleService calendar;
    private readonly OverdueReaderExportService service;
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static readonly AdminAccount Manager = new() { Id = 42, Email = "manager@example.test", Role = AccountRoles.LibraryManager };
    public OverdueReaderExportTests()
    {
        connection.Open();
        db = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).AddInterceptors(interceptor).Options);
        db.Database.EnsureCreated();
        calendar = new(db);
        service = new(db, calendar, new FixedClock(), NullLogger<OverdueReaderExportService>.Instance);
    }
    public void Dispose() { db.Dispose(); connection.Dispose(); }

    [Fact]
    public async Task OneCardPerReaderIsEnforcedByDatabase()
    {
        var type = await CardType("Chuẩn");
        var reader = await Reader(1);
        await AttachCard(reader, type);
        db.ChangeTracker.Clear();
        db.LibraryCards.Add(new LibraryCard { ReaderAccountId = reader.Id, LibraryCardTypeId = type.Id,
            CardCode = "SECOND", IssuedOn = Today, ExpiresOn = Today.AddYears(1) });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task EachTypeExportsOnlyItsReaders(int selected)
    {
        var types = new[] { await CardType("Loại một"), await CardType("Loại hai") };
        for (var i = 0; i < types.Length; i++) await AttachCard(await Reader(100 + i), types[i]);
        var result = await service.ExportAsync(Manager, "", default, null, types[selected].Id);
        Assert.Equal((100 + selected).ToString(), Assert.Single(ReadExportRows(result.Content!))[5]);
    }

    [Fact]
    public async Task CardTypeOptionsReuseActiveTypesAndExposeOnlyIdAndName()
    {
        var active = await CardType("Loại thực tế");
        await CardType("Đã ngừng", false);
        var controller = new OverdueReaderExportController(service, new AuditLogService(db, NullLogger<AuditLogService>.Instance));
        var registration = new ReaderRegistrationService(db, new PasswordHasher<ReaderAccount>(), NullLogger<ReaderRegistrationService>.Instance);
        var result = Assert.IsType<OkObjectResult>(await controller.CardTypes(registration, default));
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var item = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(active.Id, item.GetProperty("id").GetInt32());
        Assert.Equal(active.Name, item.GetProperty("name").GetString());
        Assert.Equal(2, item.EnumerateObject().Count());
    }

    [Fact]
    public async Task CombinesCardTypeAndDaysWithAndWithoutChangingAggregates()
    {
        var student = await CardType("Sinh viên");
        var other = await CardType("Loại khác");
        for (var days = 6; days <= 8; days++)
        {
            var reader = await Reader(days * 100);
            await AttachCard(reader, student);
            await Loan(reader, Today.AddDays(-days));
            if (days == 7) await Loan(reader, Today.AddDays(-2));
        }
        var different = await Reader(999);
        await AttachCard(different, other);
        await Loan(different, Today.AddDays(-8));
        var baseline = await service.GetRowsAsync(Today, default);

        var result = await service.ExportAsync(Manager, "", default, 7, student.Id);

        Assert.Equal(200, result.StatusCode);
        var rows = ReadExportRows(result.Content!);
        Assert.Equal(2, rows.Count);
        Assert.Equal([7, 8], rows.Select(r => int.Parse(r[4])).Order());
        foreach (var r in rows)
        {
            var original = Assert.Single(baseline, b => b.CardCode == r[0]);
            Assert.Equal(original.OverdueCount.ToString(), r[3]);
            Assert.Equal(original.Debt.ToString(System.Globalization.CultureInfo.InvariantCulture), r[5]);
        }
        Assert.Equal("2", Assert.Single(rows, r => r[4] == "7")[3]);
        var log = await db.AuditLogs.SingleAsync();
        Assert.Contains($"Loại thẻ: Sinh viên (ID: {student.Id})", log.Target);
        Assert.Contains("Số ngày trễ tối thiểu: 7 ngày", log.Target);
        Assert.Contains("Số dòng: 2", log.Target);
        Assert.Equal(Manager.Email, log.Actor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SpecificTypePreservesDebtOnlyRules(int? minimum)
    {
        var selected = await CardType("Loại A");
        var other = await CardType("Loại B");
        await AttachCard(await Reader(40), selected);
        await AttachCard(await Reader(50), other);
        await Reader(60);
        var result = await service.ExportAsync(Manager, "", default, minimum, selected.Id);
        if (minimum is null or 0)
        {
            var row = Assert.Single(ReadExportRows(result.Content!));
            Assert.Equal("40", row[5]);
            Assert.Equal("0", row[4]);
        }
        else
        {
            Assert.Equal(422, result.StatusCode);
            Assert.Null(result.Content);
            Assert.Equal("Không có dữ liệu phù hợp với điều kiện đã chọn", result.Error);
        }
        Assert.Contains($"(ID: {selected.Id})", (await db.AuditLogs.SingleAsync()).Target);
    }

    [Fact]
    public async Task AllTypesIncludeMissingAndInactiveTypesAndKeepDaysThreshold()
    {
        var inactive = await CardType("Cũ", false);
        var r = await Reader(10);
        await AttachCard(r, inactive);
        await Loan(r, Today.AddDays(-7));
        var noCard = await Reader(20);
        await Loan(noCard, Today.AddDays(-8));
        await Reader(30);
        var all = await service.ExportAsync(Manager, "", default);
        Assert.Equal(3, ReadExportRows(all.Content!).Count);
        var filtered = await service.ExportAsync(Manager, "", default, 7);
        Assert.Equal(2, ReadExportRows(filtered.Content!).Count);
        Assert.All(await db.AuditLogs.ToListAsync(), log => Assert.Contains("Loại thẻ: Tất cả", log.Target));
    }

    [Fact]
    public async Task UsesCurrentTypeDespiteExpiredLockedCardAndPreservesHistoricalLogName()
    {
        var original = await CardType("Loại cũ");
        var current = await CardType("Loại mới");
        var r = await Reader(123);
        var card = await AttachCard(r, original);
        card.LibraryCardTypeId = current.Id;
        card.ExpiresOn = Today.AddYears(-1);
        card.Status = "Bị khóa";
        await db.SaveChangesAsync();
        var oldResult = await service.ExportAsync(Manager, "", default, null, original.Id);
        Assert.Equal(422, oldResult.StatusCode);
        var newResult = await service.ExportAsync(Manager, "", default, null, current.Id);
        Assert.Single(ReadExportRows(newResult.Content!));
        current.Name = "Đổi tên sau xuất";
        await db.SaveChangesAsync();
        Assert.Contains(await db.AuditLogs.ToListAsync(), l => l.Target.Contains($"Loại thẻ: Loại mới (ID: {current.Id})"));
    }

    [Fact]
    public async Task ActiveButEmptyTypeIsDifferentFromTypeDeactivatedAfterOptionsLoaded()
    {
        var type = await CardType("Trống");
        var empty = await service.ExportAsync(Manager, "", default, 7, type.Id);
        Assert.Equal(422, empty.StatusCode);
        Assert.Contains("Không có dữ liệu", (await db.AuditLogs.SingleAsync()).Target);
        type.IsActive = false;
        await db.SaveChangesAsync();
        var invalid = await service.ExportAsync(Manager, "", default, 7, type.Id);
        Assert.Equal(400, invalid.StatusCode);
        Assert.Null(invalid.Content);
        Assert.Single(await db.AuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("7.5")]
    [InlineData("abc")]
    [InlineData("2147483648")]
    [InlineData("999999")]
    public async Task ControllerRejectsMalformedOrMissingCardTypes(string value)
    {
        var controller = await SignedInController();
        var result = await controller.Export(default, "7", value);
        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task OldControllerRequestsTreatMissingCardTypeAsAll(string? value)
    {
        await Reader(1);
        var controller = await SignedInController();
        Assert.IsType<FileContentResult>(await controller.Export(default, "0", value));
        Assert.Contains("Loại thẻ: Tất cả", (await db.AuditLogs.SingleAsync()).Target);
    }

    [Fact]
    public async Task CombinedControllerRequestAndAuditUseTheSameValidatedType()
    {
        var type = await CardType("Tên phía server");
        var r = await Reader(100);
        await AttachCard(r, type);
        await Loan(r, Today.AddDays(-7));
        var controller = await SignedInController();
        var file = Assert.IsType<FileContentResult>(await controller.Export(default, "7", type.Id.ToString()));
        Assert.Single(ReadExportRows(file.FileContents));
        Assert.Contains($"Tên phía server (ID: {type.Id})", (await db.AuditLogs.SingleAsync()).Target);
        interceptor.Fail = true;
        var failure = Assert.IsType<ObjectResult>(await controller.Export(default, "7", type.Id.ToString()));
        Assert.Equal(500, failure.StatusCode);
    }

    private async Task<OverdueReaderExportController> SignedInController()
    {
        const string token = "card-filter-session";
        db.AdminAccounts.Add(Manager);
        db.RefreshTokens.Add(new RefreshToken { AdminAccount = Manager, TokenHash = TokenService.HashRefreshToken(token), ExpiresAtUtc = DateTime.UtcNow.AddDays(1) });
        await db.SaveChangesAsync();
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = $"admin_refresh={token}";
        return new(service, new AuditLogService(db, NullLogger<AuditLogService>.Instance))
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private async Task<LibraryCardType> CardType(string name, bool active = true)
    {
        var type = new LibraryCardType { Name = name, IsActive = active };
        db.LibraryCardTypes.Add(type); await db.SaveChangesAsync(); return type;
    }

    private async Task<LibraryCard> AttachCard(ReaderAccount reader, LibraryCardType type)
    {
        var card = new LibraryCard { ReaderAccountId = reader.Id, LibraryCardTypeId = type.Id,
            CardCode = $"000{reader.Id}", IssuedOn = Today.AddYears(-1), ExpiresOn = Today.AddYears(1) };
        db.LibraryCards.Add(card); await db.SaveChangesAsync(); return card;
    }

    [Fact]
    public async Task AggregatesReadersWithoutMultiplyingDebtAndExcludesReturnedAndDueToday()
    {
        var reader = await Reader(12500m);
        await Loan(reader, Today.AddDays(-3));
        await Loan(reader, Today.AddDays(-1));
        await Loan(reader, Today);
        await Loan(reader, Today.AddDays(-9), true);
        var debtOnly = await Reader(800m);
        await Reader(-10m);
        var rows = await service.GetRowsAsync(Today, default);
        Assert.Equal(2, rows.Count);
        var row = Assert.Single(rows, r => r.Debt == 12500m);
        Assert.Equal(2, row.OverdueCount);
        Assert.Equal(3, row.MaxDaysOverdue);
        var debt = Assert.Single(rows, r => r.Debt == debtOnly.OutstandingBalance);
        Assert.Equal(0, debt.OverdueCount);
        Assert.Equal(0, debt.MaxDaysOverdue);
        Assert.Equal("", debt.CardCode);
    }

    [Fact]
    public async Task OpeningDaysExcludeWeeklyClosuresAndHolidays()
    {
        await calendar.UpdateWeeklyScheduleAsync(DayOfWeek.Sunday, false, null);
        await calendar.CreateHolidayClosureAsync(new(2026, 10, 12), "Nghỉ", null);
        await Loan(await Reader(0), new(2026, 10, 9));
        Assert.Equal(4, Assert.Single(await service.GetRowsAsync(Today, default)).MaxDaysOverdue);
        Assert.Empty(await service.GetRowsAsync(new(2026, 10, 9), default));
    }

    [Fact]
    public async Task CapturesVietnamDateAndPersistsSuccessBeforeReturningWorkbook()
    {
        await Reader(10);
        var result = await service.ExportAsync(Manager, "127.0.0.1", default);
        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(result.Content);
        Assert.Equal("Ban-doc-qua-han-no-phi_15-10-2026.xlsx", result.FileName);
        var log = Assert.Single(await db.AuditLogs.ToListAsync());
        Assert.Equal(Manager.Email, log.Actor);
        Assert.Equal(new DateTime(2026, 10, 14, 18, 0, 0, DateTimeKind.Utc), log.OccurredAtUtc);
        Assert.Contains("Không lọc (không áp dụng bộ lọc ngày trễ); Kết quả: Thành công; Số dòng: 1", log.Target);
    }

    [Fact]
    public async Task EmptyAndQueryFailureHaveDistinctLogsAndNoFile()
    {
        var empty = await service.ExportAsync(Manager, "", default);
        Assert.Null(empty.Content);
        Assert.Equal("Không có dữ liệu phù hợp với điều kiện đã chọn", empty.Error);
        Assert.Contains("Không có dữ liệu", (await db.AuditLogs.SingleAsync()).Target);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE BookLoans");
        var failed = await service.ExportAsync(Manager, "", default);
        Assert.Equal(500, failed.StatusCode);
        Assert.Null(failed.Content);
        Assert.Contains(await db.AuditLogs.ToListAsync(), l => l.Target.Contains("Thất bại"));
    }

    [Fact]
    public async Task ThresholdSevenUsesMaximumAfterAggregationAndKeepsAllLoanMetrics()
    {
        var six = await Reader(6);
        await Loan(six, Today.AddDays(-6));
        var seven = await Reader(700);
        await Loan(seven, Today.AddDays(-7));
        await Loan(seven, Today.AddDays(-2));
        var eight = await Reader(8);
        await Loan(eight, Today.AddDays(-8));
        await Reader(900);
        var baseline = await service.GetRowsAsync(Today, default);

        var result = await service.ExportAsync(Manager, "", default, 7);

        Assert.Equal(200, result.StatusCode);
        var actual = ReadExportRows(result.Content!);
        Assert.Equal(2, actual.Count);
        Assert.Equal([7, 8], actual.Select(r => int.Parse(r[4])).Order());
        var sevenRow = Assert.Single(actual, r => r[5] == "700");
        Assert.Equal("2", sevenRow[3]);
        Assert.Equal("7", sevenRow[4]);
        var expected = baseline.Where(r => r.MaxDaysOverdue >= 7).ToList();
        Assert.Equal(expected.Count, actual.Count);
        foreach (var row in expected)
        {
            var cells = Assert.Single(actual, a => a[5] == row.Debt.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(row.OverdueCount.ToString(), cells[3]);
            Assert.Equal(row.MaxDaysOverdue.ToString(), cells[4]);
            Assert.Equal(6, cells.Length);
        }
        var log = await db.AuditLogs.SingleAsync();
        Assert.Contains("Số ngày trễ tối thiểu: 7 ngày", log.Target);
        Assert.Contains("Số dòng: 2", log.Target);
        Assert.DoesNotContain("Không lọc", log.Target);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public async Task DebtOnlyReaderMatchesBlankAndZeroButNotPositiveThreshold(int? minimum)
    {
        await Reader(50);
        var result = await service.ExportAsync(Manager, "", default, minimum);
        var log = await db.AuditLogs.SingleAsync();
        Assert.Contains(minimum.HasValue ? $"Số ngày trễ tối thiểu: {minimum} ngày" : "Không lọc (không áp dụng bộ lọc ngày trễ)", log.Target);
        if (minimum is null or 0)
        {
            Assert.Equal(200, result.StatusCode);
            var cells = Assert.Single(ReadExportRows(result.Content!));
            Assert.Equal("0", cells[3]);
            Assert.Equal("0", cells[4]);
        }
        else
        {
            Assert.Equal(422, result.StatusCode);
            Assert.Null(result.Content);
            Assert.Equal("Không có dữ liệu phù hợp với điều kiện đã chọn", result.Error);
            Assert.Contains("Kết quả: Không có dữ liệu; Số dòng: 0", log.Target);
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  \t", null)]
    [InlineData("0", 0)]
    [InlineData(" 7 ", 7)]
    [InlineData("0007", 7)]
    [InlineData("2147483647", int.MaxValue)]
    public void ParsesOnlySupportedThresholds(string? text, int? expected)
    {
        Assert.True(OverdueReaderExportFilter.TryParse(text, out var minimum));
        Assert.Equal(expected, minimum);
    }

    [Theory]
    [InlineData(null, "Không lọc (không áp dụng bộ lọc ngày trễ)")]
    [InlineData("   ", "Không lọc (không áp dụng bộ lọc ngày trễ)")]
    [InlineData("0", "Số ngày trễ tối thiểu: 0 ngày")]
    [InlineData(" 7 ", "Số ngày trễ tối thiểu: 7 ngày")]
    public async Task ControllerForwardsAppliedThresholdToFileAndAudit(string? value, string expectedFilter)
    {
        await Loan(await Reader(100), Today.AddDays(-7));
        await Reader(20);
        const string token = "valid-export-session";
        db.AdminAccounts.Add(Manager);
        db.RefreshTokens.Add(new RefreshToken { AdminAccount = Manager,
            TokenHash = TokenService.HashRefreshToken(token), ExpiresAtUtc = DateTime.UtcNow.AddDays(1) });
        await db.SaveChangesAsync();
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = $"admin_refresh={token}";
        var controller = new OverdueReaderExportController(service, new AuditLogService(db, NullLogger<AuditLogService>.Instance))
        { ControllerContext = new ControllerContext { HttpContext = http } };

        var file = Assert.IsType<FileContentResult>(await controller.Export(default, value));

        Assert.Equal(OverdueReaderExportService.ContentType, file.ContentType);
        Assert.Equal("Ban-doc-qua-han-no-phi_15-10-2026.xlsx", file.FileDownloadName);
        var count = value?.Trim() == "7" ? 1 : 2;
        Assert.Equal(count, ReadExportRows(file.FileContents).Count);
        var log = await db.AuditLogs.SingleAsync();
        Assert.Contains(expectedFilter + "; Kết quả: Thành công", log.Target);
        Assert.Contains($"Số dòng: {count}", log.Target);
        Assert.Equal(Manager.Email, log.Actor);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("7.5")]
    [InlineData("7.0")]
    [InlineData("abc")]
    [InlineData("2147483648")]
    [InlineData("99999999999999999999999999999")]
    [InlineData("1e2")]
    [InlineData("+7")]
    [InlineData("7,5")]
    public async Task DirectControllerCallRejectsInvalidThresholdWithoutFileOrAudit(string text)
    {
        var token = "export-test-session";
        db.AdminAccounts.Add(Manager);
        db.RefreshTokens.Add(new RefreshToken { AdminAccount = Manager,
            TokenHash = TokenService.HashRefreshToken(token), ExpiresAtUtc = DateTime.UtcNow.AddDays(1) });
        await db.SaveChangesAsync();
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = $"admin_refresh={token}";
        var controller = new OverdueReaderExportController(service, new AuditLogService(db, NullLogger<AuditLogService>.Instance))
        { ControllerContext = new ControllerContext { HttpContext = http } };

        var result = await controller.Export(default, text);

        var error = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains(OverdueReaderExportFilter.ValidationMessage, error.Value!.ToString());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task FilteredExportStillBlocksFileWhenAuditFails()
    {
        await Loan(await Reader(7), Today.AddDays(-7));
        interceptor.Fail = true;
        var result = await service.ExportAsync(Manager, "", default, 7);
        Assert.Equal(500, result.StatusCode);
        Assert.Null(result.Content);
        Assert.Contains("nhật ký", result.Error);
    }

    private static List<string[]> ReadExportRows(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var input = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return XDocument.Load(input).Descendants(ns + "row").Skip(1)
            .Select(r => r.Elements(ns + "c").Select(c => c.Element(ns + "v")?.Value ?? c.Descendants(ns + "t").Single().Value).ToArray()).ToList();
    }

    [Fact]
    public async Task WorkbookGenerationFailureIsAuditedWithoutReturningFile()
    {
        var reader = await Reader(10);
        reader.FullName = "Invalid XML \u0001";
        await db.SaveChangesAsync();
        var result = await service.ExportAsync(Manager, "", default);
        Assert.Null(result.Content);
        Assert.Equal(500, result.StatusCode);
        Assert.Contains("Thất bại", (await db.AuditLogs.SingleAsync()).Target);
    }

    [Fact]
    public async Task AuditFailureBlocksPreparedFile()
    {
        await Reader(10);
        interceptor.Fail = true;
        var result = await service.ExportAsync(Manager, "", default);
        Assert.Equal(500, result.StatusCode);
        Assert.Null(result.Content);
        Assert.Contains("nhật ký", result.Error);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData(AccountRoles.Librarian)]
    public async Task OtherRolesCannotExport(string role)
    {
        var result = await service.ExportAsync(new AdminAccount { Role = role }, "", default);
        Assert.Equal(403, result.StatusCode);
        Assert.Null(result.Content);
        Assert.Empty(await db.AuditLogs.ToListAsync());
        var filter = Assert.Single(typeof(OverdueReaderExportController).GetCustomAttributes(typeof(StaffOnlyAttribute), true).Cast<StaffOnlyAttribute>());
        Assert.Equal([AccountRoles.LibraryManager, AccountRoles.SystemAdmin].Order(), filter.Roles.Order());
    }

    [Theory]
    [InlineData("/api/overdue-readers/export")]
    [InlineData("/api/overdue-readers/card-types")]
    public async Task AnonymousApiRequestIsBlockedBeforeAction(string path)
    {
        using var provider = new ServiceCollection().AddSingleton<IAuditLogService>(new AuditLogService(db, NullLogger<AuditLogService>.Instance)).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = provider };
        http.Request.Path = path;
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(action, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());
        await new StaffOnlyAttribute(AccountRoles.LibraryManager).OnActionExecutionAsync(context,
            () => throw new Exception("Action must not execute"));
        Assert.IsType<UnauthorizedObjectResult>(context.Result);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public void WorkbookHasSixTypedColumnsAndLiteralText()
    {
        var bytes = OverdueReaderWorkbook.Create([new("00123", "=HYPERLINK(\"x\") Nguyễn", "0900123456", 2, 3, 1234.5m)]);
        using var archive = new ZipArchive(new MemoryStream(bytes));
        XDocument Read(string path) { using var input = archive.GetEntry(path)!.Open(); return XDocument.Load(input); }
        foreach (var entry in archive.Entries) _ = Read(entry.FullName);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var sheet = Read("xl/worksheets/sheet1.xml");
        var rows = sheet.Descendants(ns + "row").ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(OverdueReaderWorkbook.Headers, rows[0].Descendants(ns + "t").Select(t => t.Value));
        var cells = rows[1].Elements(ns + "c").ToArray();
        Assert.Equal(6, cells.Length);
        Assert.Equal("00123", cells[0].Descendants(ns + "t").Single().Value);
        Assert.Equal("0900123456", cells[2].Descendants(ns + "t").Single().Value);
        Assert.All(cells.Take(3), c => Assert.Equal("inlineStr", c.Attribute("t")?.Value));
        Assert.Empty(sheet.Descendants(ns + "f"));
        Assert.Equal(["2", "3", "1234.5"], cells.Skip(3).Select(c => c.Element(ns + "v")!.Value));
        Assert.Equal("2", cells[5].Attribute("s")!.Value);
        Assert.NotNull(archive.GetEntry("[Content_Types].xml"));
    }

    private async Task<ReaderAccount> Reader(decimal debt)
    {
        var key = Guid.NewGuid().ToString("N");
        var reader = new ReaderAccount { FullName = "Bạn đọc", PhoneNumber = "0900000000", Email = key + "@example.test", StudentOrStaffCode = key, PasswordHash = "x", OutstandingBalance = debt, DateOfBirth = new(2000, 1, 1) };
        db.ReaderAccounts.Add(reader); await db.SaveChangesAsync(); return reader;
    }
    private async Task Loan(ReaderAccount reader, DateOnly due, bool returned = false)
    {
        var book = new Book { Title = "Sách", Author = new Author { Name = Guid.NewGuid().ToString() } };
        db.Books.Add(book); await db.SaveChangesAsync();
        db.BookLoans.Add(new BookLoan { BookId = book.Id, ReaderAccountId = reader.Id, LoanDate = due.AddDays(-14), OriginalDueDate = due, DueDate = due, ReturnedAtUtc = returned ? DateTime.UtcNow : null });
        await db.SaveChangesAsync();
    }
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 14, 18, 0, 0, TimeSpan.Zero);
    }
    private sealed class RejectAudit : SaveChangesInterceptor
    {
        public bool Fail { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Fail && eventData.Context!.ChangeTracker.Entries<AuditLog>().Any(e => e.State == EntityState.Added))
                throw new InvalidOperationException("Audit storage unavailable");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
