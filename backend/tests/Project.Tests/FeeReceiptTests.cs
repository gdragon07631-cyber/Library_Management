using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Project.Controllers;
using Project.Data;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class FeeReceiptTests
{
    [Theory]
    [InlineData(80000, 1)]
    [InlineData(200000, 2)]
    public async Task SuccessfulPayment_CreatesOneStableReceiptWithActualAllocations(int amount, int lineCount)
    {
        await using var f = await Fixture.CreateAsync();
        var start = DateTime.UtcNow;
        var request = f.Request(amount);
        var payment = await f.Payments.CollectAsync(request, f.StaffId);
        Assert.True(payment.IsSuccess, payment.Message);
        var result = await f.Receipts.GetAsync(payment.PaymentId!.Value);
        var receipt = Assert.IsType<FeeReceiptViewModel>(result.Receipt);
        Assert.Equal($"BL-{payment.PaymentId:D8}", receipt.Number);
        Assert.Equal("Nguyễn Văn An", receipt.ReaderName);
        Assert.Equal("Trần Thị Bình", receipt.CollectedByName);
        Assert.InRange(receipt.PaidAtUtc, start, DateTime.UtcNow);
        Assert.Equal(TimeSpan.FromHours(7), receipt.PaidAtLocal.Offset);
        Assert.Equal(receipt.PaidAtUtc, receipt.PaidAtLocal.UtcDateTime);
        Assert.Equal(amount, receipt.Amount);
        Assert.Equal(amount, receipt.Lines.Sum(l => l.Amount));
        Assert.Equal(lineCount, receipt.Lines.Count);
        Assert.Equal("Phí trễ hạn", receipt.Lines[0].Description);
        Assert.Equal(amount == 80000 ? 80000m : 120000m, receipt.Lines[0].Amount);
        Assert.True((await f.Payments.CollectAsync(request, f.StaffId)).IsSuccess);
        var again = (await f.Receipts.GetAsync(payment.PaymentId.Value)).Receipt!;
        Assert.Equal(receipt.Number, again.Number);
        Assert.Single(await f.Db.FeeReceipts.ToListAsync());
        Assert.Single(await f.Db.FeePayments.ToListAsync());
    }

    [Fact]
    public async Task SnapshotNamesAndDescriptions_SurviveLaterDataChanges()
    {
        await using var f = await Fixture.CreateAsync();
        var payment = await f.Payments.CollectAsync(f.Request(80000m), f.StaffId);
        Assert.True(payment.IsSuccess, payment.Message);
        var before = (await f.Receipts.GetAsync(payment.PaymentId!.Value)).Receipt!;
        await f.Db.ReaderAccounts.ExecuteUpdateAsync(u => u.SetProperty(r => r.FullName, "Tên bạn đọc mới"));
        await f.Db.AdminAccounts.ExecuteUpdateAsync(u => u.SetProperty(r => r.FullName, "Tên thủ thư mới"));
        await f.Db.ReaderFees.ExecuteUpdateAsync(u => u.SetProperty(r => r.Description, "Mô tả mới").SetProperty(r => r.Amount, 900000m));
        await using var fresh = new ApplicationDbContext(f.Options);
        var after = (await new FeeReceiptService(fresh).GetAsync(payment.PaymentId.Value)).Receipt!;
        Assert.Equal(before.Number, after.Number);
        Assert.Equal(before.ReaderName, after.ReaderName);
        Assert.Equal(before.CollectedByName, after.CollectedByName);
        Assert.Equal(before.PaidAtUtc, after.PaidAtUtc);
        Assert.Equal(before.Amount, after.Amount);
        Assert.Equal(before.Lines.ToArray(), after.Lines.ToArray());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(200001)]
    public async Task InvalidPayment_CreatesNoReceipt(int amount)
    {
        await using var f = await Fixture.CreateAsync();
        Assert.False((await f.Payments.CollectAsync(f.Request(amount), f.StaffId)).IsSuccess);
        Assert.Empty(await f.Db.FeeReceipts.ToListAsync());
        Assert.Empty(await f.Db.FeeReceiptLines.ToListAsync());
        Assert.Empty(await f.Db.FeePayments.ToListAsync());
    }

    [Theory]
    [InlineData("FeeReceipts")]
    [InlineData("FeeReceiptLines")]
    public async Task ReceiptWriteFailure_RollsBackPaymentDebtAndCard(string table)
    {
        await using var f = await Fixture.CreateAsync();
        var trigger = table == "FeeReceipts"
            ? "CREATE TRIGGER FailReceipt BEFORE INSERT ON FeeReceipts BEGIN SELECT RAISE(ABORT, 'receipt save failed'); END;"
            : "CREATE TRIGGER FailReceipt BEFORE INSERT ON FeeReceiptLines BEGIN SELECT RAISE(ABORT, 'receipt save failed'); END;";
        await f.Db.Database.ExecuteSqlRawAsync(trigger);
        var request = f.Request(200000m);
        Assert.False((await f.Payments.CollectAsync(request, f.StaffId)).IsSuccess);
        var debt = (await f.Payments.GetDebtAsync(f.ReaderId))!;
        Assert.Equal(200000m, debt.Balance);
        Assert.Equal("Nợ phí", debt.CardStatus);
        Assert.All(debt.Fees, fee => Assert.Equal(0m, fee.PaidAmount));
        Assert.Empty(await f.Db.FeePayments.ToListAsync());
        Assert.Empty(await f.Db.FeePaymentAllocations.ToListAsync());
        Assert.Empty(await f.Db.FeeReceipts.ToListAsync());
        Assert.Empty(await f.Db.FeeReceiptLines.ToListAsync());
        await f.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailReceipt;");
        Assert.True((await f.Payments.CollectAsync(request, f.StaffId)).IsSuccess);
    }

    [Fact]
    public async Task ReadingAndReprinting_DoesNotChangeDebtCardOrHistory()
    {
        await using var f = await Fixture.CreateAsync();
        var first = await f.Payments.CollectAsync(f.Request(80000m), f.StaffId);
        var second = await f.Payments.CollectAsync(f.Request(120000m), f.StaffId);
        Assert.True(first.IsSuccess, first.Message);
        Assert.True(second.IsSuccess, second.Message);
        var receiptNumbers = new HashSet<string>();
        for (var i = 0; i < 5; i++)
        {
            receiptNumbers.Add((await f.Receipts.GetAsync(first.PaymentId!.Value)).Receipt!.Number);
            receiptNumbers.Add((await f.Receipts.GetAsync(second.PaymentId!.Value)).Receipt!.Number);
        }
        Assert.Equal(2, receiptNumbers.Count);
        Assert.Equal(2, await f.Db.FeePayments.CountAsync());
        Assert.Equal(2, await f.Db.FeeReceipts.CountAsync());
        Assert.Equal(3, await f.Db.FeePaymentAllocations.CountAsync());
        var debt = (await f.Payments.GetDebtAsync(f.ReaderId))!;
        Assert.Equal(0m, debt.Balance);
        Assert.Equal("Đang hoạt động", debt.CardStatus);
        Assert.All(debt.Fees, fee => Assert.Equal(0m, fee.RemainingAmount));
    }

    [Fact]
    public async Task MissingPaymentAndLegacyPayment_AreNotFabricated()
    {
        await using var f = await Fixture.CreateAsync();
        var missing = await f.Receipts.GetAsync(long.MaxValue);
        Assert.Null(missing.Receipt);
        Assert.False(missing.PaymentExists);
        // Existing pre-snapshot transaction: do not backfill today's names into a historical receipt.
        var legacy = new FeePayment { RequestId = Guid.NewGuid(), ReaderAccountId = f.ReaderId,
            CollectedByAdminAccountId = f.StaffId, Amount = 1000m, PaidAtUtc = DateTime.UtcNow.AddDays(-3), BalanceAfter = 200000m };
        f.Db.FeePayments.Add(legacy);
        await f.Db.SaveChangesAsync();
        var result = await f.Receipts.GetAsync(legacy.Id);
        Assert.True(result.PaymentExists);
        Assert.Null(result.Receipt);
        Assert.Contains("chưa có bản chụp", result.Error);
        Assert.Empty(await f.Db.FeeReceipts.ToListAsync());
    }

    [Fact]
    public void MigrationMatchesModelAndOnlyAddsSnapshotTables()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ReceiptMetadataOnly;Integrated Security=true").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var script = db.GetService<IMigrator>().GenerateScript("20261020000000_AddFeePayments", "20261021000000_AddFeeReceipts");
        Assert.Contains("CREATE TABLE [FeeReceipts]", script);
        Assert.Contains("CREATE TABLE [FeeReceiptLines]", script);
        Assert.Contains("PRIMARY KEY ([FeePaymentId])", script);
        Assert.DoesNotContain("UPDATE [", script);
        Assert.DoesNotContain("ALTER TABLE [FeePayments]", script);
        Assert.DoesNotContain("ALTER TABLE [ReaderAccounts]", script);
        Assert.Equal(new[] { AccountRoles.Librarian }, typeof(FeeReceiptController).GetCustomAttribute<StaffOnlyAttribute>()!.Roles);
    }

    [Fact]
    public async Task HttpReceipt_PartialFullReprintPermissionsAndMissingPayment()
    {
        await using var f = await Fixture.CreateAsync();
        var token = Guid.NewGuid().ToString();
        f.Db.RefreshTokens.Add(new() { AdminAccountId = f.StaffId, TokenHash = TokenService.HashRefreshToken(token),
            CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddHours(1) });
        await f.Db.SaveChangesAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(FeeReceiptController).Assembly.GetName().Name, EnvironmentName = "Development"
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllersWithViews();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddScoped(_ => new ApplicationDbContext(f.Options));
        builder.Services.AddScoped<IFeePaymentService, FeePaymentService>();
        builder.Services.AddScoped<FeeReceiptService>();
        builder.Services.AddScoped<IAuditLogService, AuditLogService>();
        await using var app = builder.Build();
        app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
        await app.StartAsync();
        try
        {
            var uri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            var cookies = new CookieContainer();
            cookies.Add(uri, new Cookie("admin_refresh", token));
            using var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies, UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = uri };
            long firstId = 0;
            foreach (var amount in new[] { 80000m, 120000m })
            {
                var payment = await f.Payments.CollectAsync(f.Request(amount), f.StaffId);
                Assert.True(payment.IsSuccess, payment.Message);
                if (firstId == 0) firstId = payment.PaymentId!.Value;
                var path = $"/FeeReceipt/Details?paymentId={payment.PaymentId}";
                var html = await client.GetStringAsync(path);
                var decoded = WebUtility.HtmlDecode(html);
                Assert.Contains("BIÊN LAI THU PHÍ", decoded);
                Assert.Contains("Nguyễn Văn An", decoded);
                Assert.Contains("Trần Thị Bình", decoded);
                Assert.Contains("Phí trễ hạn", decoded);
                Assert.Contains("UTC+7", decoded);
                Assert.Contains("window.print()", decoded);
                Assert.Contains(amount == 80000m ? "80.000,00" : "120.000,00", decoded);
                Assert.Equal(html, await client.GetStringAsync(path));
                // Print QA artifact contains only synthetic test data; no real payment information.
                if (Environment.GetEnvironmentVariable("FEE_RECEIPT_QA_DIR") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    await File.WriteAllTextAsync(Path.Combine(directory, $"receipt-{amount:0}.html"), html);
                }
            }
            var history = WebUtility.HtmlDecode(await client.GetStringAsync($"/FeePayment?readerId={f.ReaderId}"));
            Assert.Equal(2, Regex.Matches(history, "Xem biên lai").Count);
            using var missing = await client.GetAsync("/FeeReceipt/Details?paymentId=9223372036854775807");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Contains("Không tìm thấy", WebUtility.HtmlDecode(await missing.Content.ReadAsStringAsync()));
            using var anonymous = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = uri };
            anonymous.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            using var denied = await anonymous.GetAsync($"/FeeReceipt/Details?paymentId={firstId}");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            foreach (var role in new[] { AccountRoles.LibraryManager, AccountRoles.SystemAdmin })
            {
                await f.Db.AdminAccounts.ExecuteUpdateAsync(u => u.SetProperty(a => a.Role, role));
                using var forbidden = await client.GetAsync($"/FeeReceipt/Details?paymentId={firstId}");
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
                Assert.DoesNotContain("Nguyễn Văn An", WebUtility.HtmlDecode(await forbidden.Content.ReadAsStringAsync()));
            }
            Assert.Equal(2, await f.Db.FeePayments.CountAsync());
            Assert.Equal(0m, (await f.Payments.GetDebtAsync(f.ReaderId))!.Balance);
        }
        finally { await app.StopAsync(); }
    }

    private sealed class Fixture(SqliteConnection connection, ApplicationDbContext db, DbContextOptions<ApplicationDbContext> options, int readerId, int staffId) : IAsyncDisposable
    {
        public ApplicationDbContext Db => db;
        public DbContextOptions<ApplicationDbContext> Options => options;
        public int ReaderId => readerId;
        public int StaffId => staffId;
        public FeePaymentService Payments => new(db);
        public FeeReceiptService Receipts => new(db);
        public CollectFeeViewModel Request(decimal amount) => new() { ReaderAccountId = readerId, Amount = amount, Confirmed = true, RequestId = Guid.NewGuid() };
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
            var db = new ApplicationDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var reader = new ReaderAccount { FullName = "Nguyễn Văn An", Email = "reader@receipt.test", PhoneNumber = "0900000000",
                StudentOrStaffCode = "RECEIPT", PasswordHash = "test", DateOfBirth = new(2000, 1, 1), OutstandingBalance = 200000m };
            var staff = new AdminAccount { FullName = "Trần Thị Bình", Email = "staff@receipt.test", PasswordHash = "test", Role = AccountRoles.Librarian };
            db.AddRange(reader, staff);
            db.LibraryCards.Add(new() { ReaderAccount = reader, CardCode = "RECEIPT-CARD", LibraryCardType = new() { Name = "Receipt test" },
                IssuedOn = new(2026, 1, 1), ExpiresOn = new(2027, 1, 1), Status = "Nợ phí" });
            db.ReaderFees.AddRange(new ReaderFee { ReaderAccount = reader, Amount = 120000m, Description = "Phí trễ hạn", CreatedAtUtc = DateTime.UtcNow.AddDays(-2) },
                new ReaderFee { ReaderAccount = reader, Amount = 80000m, Description = "Phí tài liệu", CreatedAtUtc = DateTime.UtcNow.AddDays(-1) });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new(connection, db, options, reader.Id, staff.Id);
        }
        public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
