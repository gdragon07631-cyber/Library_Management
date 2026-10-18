using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Project.Controllers;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class FeePaymentSlice2Tests
{
    [Fact]
    public async Task Demo_PartialThenFull_AllocatesAndPreservesEachHistoryEntry()
    {
        await using var f = await Fixture.CreateAsync();
        var start = DateTime.UtcNow;
        var firstRequest = f.Request(80000m);
        var first = await f.Service.CollectAsync(firstRequest, f.StaffId);
        Assert.True(first.IsSuccess, first.Message);
        var debt = (await f.Service.GetDebtAsync(f.ReaderId))!;
        Assert.Equal(120000m, debt.Balance);
        Assert.Equal("Nợ phí", debt.CardStatus);
        Assert.Equal(new[] { 80000m, 0m }, debt.Fees.Select(x => x.PaidAmount));
        Assert.Equal(new[] { 40000m, 80000m }, debt.Fees.Select(x => x.RemainingAmount));
        var second = await f.Service.CollectAsync(f.Request(120000m), f.StaffId);
        Assert.True(second.IsSuccess, second.Message);
        debt = (await f.Service.GetDebtAsync(f.ReaderId))!;
        Assert.Equal(0m, debt.Balance);
        Assert.Equal("Đang hoạt động", debt.CardStatus);
        Assert.All(debt.Fees, fee => Assert.Equal(0m, fee.RemainingAmount));
        var history = await f.Db.FeePayments.AsNoTracking().Include(p => p.Allocations).OrderBy(p => p.Id).ToListAsync();
        Assert.Equal(new[] { 80000m, 120000m }, history.Select(p => p.Amount));
        Assert.Equal(new[] { 120000m, 0m }, history.Select(p => p.BalanceAfter));
        Assert.All(history, p =>
        {
            Assert.Equal(f.StaffId, p.CollectedByAdminAccountId);
            Assert.InRange(p.PaidAtUtc, start, DateTime.UtcNow);
            Assert.Equal(p.Amount, p.Allocations.Sum(a => a.Amount));
        });
        Assert.Equal(new[] { 40000m, 80000m }, history[1].Allocations.OrderBy(a => a.ReaderFeeId).Select(a => a.Amount));
        Assert.True((await f.Service.CollectAsync(firstRequest, f.StaffId)).IsSuccess);
        Assert.Equal(2, await f.Db.FeePayments.CountAsync());
        Assert.Equal(0m, (await f.Service.GetDebtAsync(f.ReaderId))!.Balance);
    }

    [Theory]
    [InlineData("Nợ phí", "Đang hoạt động")]
    [InlineData("Bị khoá", "Bị khoá")]
    [InlineData("Bị khóa - Nợ phí", "Bị khóa - Nợ phí")]
    [InlineData("Hết hạn", "Hết hạn")]
    [InlineData("Đang hoạt động", "Đang hoạt động")]
    public async Task FullPayment_OnlyClearsStandaloneDebtStatus(string before, string after)
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.LibraryCards.ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, before));
        var request = f.Request(200000m);
        Assert.True((await f.Service.CollectAsync(request, f.StaffId)).IsSuccess);
        Assert.Equal(after, (await f.Service.GetDebtAsync(f.ReaderId))!.CardStatus);
        Assert.True((await f.Service.CollectAsync(request, f.StaffId)).IsSuccess);
        Assert.Single(await f.Db.FeePayments.ToListAsync());
        Assert.Equal(after, (await f.Service.GetDebtAsync(f.ReaderId))!.CardStatus);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(200001)]
    public async Task InvalidAmounts_DoNotChangeDebtCardOrHistory(int amount)
    {
        await using var f = await Fixture.CreateAsync();
        Assert.False((await f.Service.CollectAsync(f.Request(amount), f.StaffId)).IsSuccess);
        var debt = (await f.Service.GetDebtAsync(f.ReaderId))!;
        Assert.Equal(200000m, debt.Balance);
        Assert.Equal("Nợ phí", debt.CardStatus);
        Assert.All(debt.Fees, fee => Assert.Equal(0m, fee.PaidAmount));
        Assert.Empty(await f.Db.FeePayments.ToListAsync());
    }

    [Theory]
    [InlineData("CREATE TRIGGER FailPayment BEFORE INSERT ON FeePayments BEGIN SELECT RAISE(ABORT, 'payment failure'); END;")]
    [InlineData("CREATE TRIGGER FailCard BEFORE UPDATE OF Status ON LibraryCards BEGIN SELECT RAISE(ABORT, 'card failure'); END;")]
    public async Task Failure_RollsBackFeesBalanceCardAndHistory(string trigger)
    {
        await using var f = await Fixture.CreateAsync();
        Assert.True((await f.Service.CollectAsync(f.Request(80000m), f.StaffId)).IsSuccess);
        await f.Db.Database.ExecuteSqlRawAsync(trigger);
        Assert.False((await f.Service.CollectAsync(f.Request(120000m), f.StaffId)).IsSuccess);
        var debt = (await f.Service.GetDebtAsync(f.ReaderId))!;
        Assert.Equal(120000m, debt.Balance);
        Assert.Equal("Nợ phí", debt.CardStatus);
        Assert.Equal(new[] { 80000m, 0m }, debt.Fees.Select(x => x.PaidAmount));
        Assert.Single(await f.Db.FeePayments.ToListAsync());
        Assert.Single(await f.Db.FeePaymentAllocations.ToListAsync());
    }

    [Fact]
    public async Task LockedAccountAndCardValidity_AreNotChangedByPayment()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.ReaderAccounts.ExecuteUpdateAsync(u => u.SetProperty(r => r.IsLocked, true));
        var oldExpiry = new DateOnly(2020, 1, 1);
        await f.Db.LibraryCards.ExecuteUpdateAsync(u => u.SetProperty(c => c.ExpiresOn, oldExpiry).SetProperty(c => c.Status, "Hết hạn"));
        Assert.True((await f.Service.CollectAsync(f.Request(200000m), f.StaffId)).IsSuccess);
        Assert.True((await f.Db.ReaderAccounts.AsNoTracking().SingleAsync()).IsLocked);
        var card = await f.Db.LibraryCards.AsNoTracking().SingleAsync();
        Assert.Equal(oldExpiry, card.ExpiresOn);
        Assert.Equal("Hết hạn", card.Status);
    }

    [Fact]
    public async Task InvalidSessionRoleOrRequestKey_DoesNotCollect()
    {
        await using var f = await Fixture.CreateAsync();
        foreach (var role in new[] { AccountRoles.SystemAdmin, AccountRoles.LibraryManager })
        {
            await f.Db.AdminAccounts.ExecuteUpdateAsync(u => u.SetProperty(a => a.Role, role));
            Assert.False((await f.Service.CollectAsync(f.Request(200000m), f.StaffId)).IsSuccess);
        }
        await f.Db.AdminAccounts.ExecuteUpdateAsync(u => u.SetProperty(a => a.Role, AccountRoles.Librarian).SetProperty(a => a.IsActive, false));
        Assert.False((await f.Service.CollectAsync(f.Request(200000m), f.StaffId)).IsSuccess);
        await f.Db.AdminAccounts.ExecuteUpdateAsync(u => u.SetProperty(a => a.IsActive, true));
        var missingKey = f.Request(200000m);
        missingKey.RequestId = Guid.Empty;
        Assert.False((await f.Service.CollectAsync(missingKey, f.StaffId)).IsSuccess);
        Assert.Empty(await f.Db.FeePayments.ToListAsync());
        Assert.Equal("Nợ phí", (await f.Service.GetDebtAsync(f.ReaderId))!.CardStatus);
    }

    [Fact]
    public async Task NoCard_FullPaymentStillSucceeds()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.LibraryCards.ExecuteDeleteAsync();
        Assert.True((await f.Service.CollectAsync(f.Request(200000m), f.StaffId)).IsSuccess);
        var debt = (await f.Service.GetDebtAsync(f.ReaderId))!;
        Assert.Null(debt.CardStatus);
        Assert.Equal(0m, debt.Balance);
    }

    [Fact]
    public async Task HttpDemo_RendersPartialAndFullPaymentHistoryAndStatus()
    {
        await using var f = await Fixture.CreateAsync();
        var token = Guid.NewGuid().ToString();
        f.Db.RefreshTokens.Add(new() { AdminAccountId = f.StaffId, TokenHash = TokenService.HashRefreshToken(token),
            CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddHours(1) });
        await f.Db.SaveChangesAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(FeePaymentController).Assembly.GetName().Name, EnvironmentName = "Development"
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllersWithViews();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddScoped(_ => new ApplicationDbContext(f.Options));
        builder.Services.AddScoped<IFeePaymentService, FeePaymentService>();
        builder.Services.AddScoped<IAuditLogService, AuditLogService>();
        await using var app = builder.Build();
        app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
        await app.StartAsync();
        try
        {
            var uri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var anonymous = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = uri };
            using var denied = await anonymous.GetAsync("/FeePayment");
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
            var cookies = new CookieContainer();
            cookies.Add(uri, new Cookie("admin_refresh", token));
            using var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies, UseProxy = false }) { BaseAddress = uri };
            var html = await client.GetStringAsync($"/FeePayment?readerId={f.ReaderId}");
            using var missingCsrf = await client.PostAsync("/FeePayment/Collect", new FormUrlEncodedContent(new Dictionary<string, string>()));
            Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
            foreach (var amount in new[] { "80000", "120000" })
            {
                var fields = new Dictionary<string, string>
                {
                    ["ReaderAccountId"] = f.ReaderId.ToString(), ["RequestId"] = Input(html, "RequestId"),
                    ["__RequestVerificationToken"] = Input(html, "__RequestVerificationToken"),
                    ["Amount"] = amount, ["Confirmed"] = "true"
                };
                using var response = await client.PostAsync("/FeePayment/Collect", new FormUrlEncodedContent(fields));
                response.EnsureSuccessStatusCode();
                html = await response.Content.ReadAsStringAsync();
                var decoded = WebUtility.HtmlDecode(html);
                Assert.Contains("Lịch sử thanh toán", decoded);
                Assert.Contains("Librarian Slice 2", decoded);
                if (amount == "80000")
                {
                    Assert.Contains("120.000,00", decoded);
                    Assert.Contains("<strong>Nợ phí</strong>", decoded);
                }
                else
                {
                    Assert.Contains("Bạn đọc không có khoản nợ", decoded);
                    Assert.Contains("<strong>Đang hoạt động</strong>", decoded);
                    Assert.Contains("80.000,00", decoded);
                    Assert.Contains("120.000,00", decoded);
                    Assert.Equal(2, await f.Db.FeePayments.CountAsync());
                    var historyTable = Regex.Match(decoded, "<table[^>]*id=\"fee-payment-history\"[^>]*>(.*?)</table>", RegexOptions.Singleline).Value;
                    Assert.Equal(3, Regex.Matches(historyTable, "<tr>").Count);
                }
            }
        }
        finally { await app.StopAsync(); }
        static string Input(string html, string name) => WebUtility.HtmlDecode(Regex.Match(
            Regex.Match(html, $"<input[^>]*name=\"{name}\"[^>]*>").Value, "value=\"([^\"]+)\"").Groups[1].Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentFullPayments_OnlyOneTransactionIsRecorded(bool sameRequest)
    {
        var path = Path.Combine(Path.GetTempPath(), $"slice2-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite($"Data Source={path};Pooling=False;Default Timeout=5").Options;
        try
        {
            int readerId, staffId;
            await using (var seed = new ApplicationDbContext(options))
            {
                await seed.Database.EnsureCreatedAsync();
                (readerId, staffId) = await Fixture.SeedAsync(seed);
            }
            var key = Guid.NewGuid();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int arrived = 0;
            async Task<FeePaymentResult> Submit()
            {
                await using var db = new ApplicationDbContext(options);
                if (Interlocked.Increment(ref arrived) == 2) ready.SetResult();
                await ready.Task;
                return await new FeePaymentService(db).CollectAsync(new()
                {
                    ReaderAccountId = readerId, Amount = 200000m, Confirmed = true, RequestId = sameRequest ? key : Guid.NewGuid()
                }, staffId);
            }
            var results = await Task.WhenAll(Task.Run(Submit), Task.Run(Submit));
            Assert.Contains(results, r => r.IsSuccess);
            await using var verify = new ApplicationDbContext(options);
            Assert.Single(await verify.FeePayments.ToListAsync());
            var debt = (await new FeePaymentService(verify).GetDebtAsync(readerId))!;
            Assert.Equal(0m, debt.Balance);
            Assert.Equal("Đang hoạt động", debt.CardStatus);
            Assert.All(debt.Fees, fee => Assert.Equal(0m, fee.RemainingAmount));
        }
        finally { File.Delete(path); }
    }

    private sealed class Fixture(SqliteConnection connection, ApplicationDbContext db, DbContextOptions<ApplicationDbContext> options, int readerId, int staffId) : IAsyncDisposable
    {
        public ApplicationDbContext Db => db;
        public DbContextOptions<ApplicationDbContext> Options => options;
        public int ReaderId => readerId;
        public int StaffId => staffId;
        public FeePaymentService Service => new(db);
        public CollectFeeViewModel Request(decimal amount) => new() { ReaderAccountId = readerId, Amount = amount, Confirmed = true, RequestId = Guid.NewGuid() };
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
            var db = new ApplicationDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var (readerId, staffId) = await SeedAsync(db);
            return new(connection, db, options, readerId, staffId);
        }
        public static async Task<(int, int)> SeedAsync(ApplicationDbContext db)
        {
            var reader = new ReaderAccount { FullName = "Reader Slice 2", Email = "reader@slice2.test", PhoneNumber = "0900000000",
                StudentOrStaffCode = "SLICE2", PasswordHash = "test", DateOfBirth = new(2000, 1, 1), OutstandingBalance = 200000m };
            var staff = new AdminAccount { FullName = "Librarian Slice 2", Email = "librarian@slice2.test", PasswordHash = "test", Role = AccountRoles.Librarian };
            db.AddRange(reader, staff);
            db.LibraryCards.Add(new() { ReaderAccount = reader, CardCode = "SLICE2-CARD", LibraryCardType = new() { Name = "Slice 2 test" },
                IssuedOn = new(2026, 1, 1), ExpiresOn = new(2027, 1, 1), Status = "Nợ phí" });
            db.ReaderFees.AddRange(new ReaderFee { ReaderAccount = reader, Amount = 120000m, CreatedAtUtc = DateTime.UtcNow.AddDays(-2) },
                new ReaderFee { ReaderAccount = reader, Amount = 80000m, CreatedAtUtc = DateTime.UtcNow.AddDays(-1) });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return (reader.Id, staff.Id);
        }
        public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
