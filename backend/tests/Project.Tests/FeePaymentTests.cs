using System.Reflection;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Project.Controllers;
using Project.Data;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class FeePaymentTests
{
    [Fact]
    public async Task HttpFlow_RendersPage_EnforcesSessionAndCsrf_PartialReplayAndFullPayment()
    {
        await using var f = await Fixture.CreateAsync();
        var token = Guid.NewGuid().ToString();
        f.Db.RefreshTokens.Add(new RefreshToken
        {
            AdminAccountId = f.StaffId, TokenHash = TokenService.HashRefreshToken(token),
            CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        });
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
            Assert.Contains("Account/Login", denied.Headers.Location!.ToString());
            var cookies = new CookieContainer();
            cookies.Add(uri, new Cookie("admin_refresh", token));
            using var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies, UseProxy = false }) { BaseAddress = uri };
            var html = await client.GetStringAsync($"/FeePayment?search=PAY-CARD&readerId={f.ReaderId}");
            Assert.Contains("Payment Reader", html);
            Assert.Contains("PAY-CARD", html);
            Assert.Contains("9.000,00", html);
            var fields = new Dictionary<string, string>
            {
                ["ReaderAccountId"] = f.ReaderId.ToString(), ["RequestId"] = Input(html, "RequestId"),
                ["Amount"] = "3000", ["Confirmed"] = "true"
            };
            using var missingCsrf = await client.PostAsync("/FeePayment/Collect", new FormUrlEncodedContent(fields));
            Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
            fields["__RequestVerificationToken"] = Input(html, "__RequestVerificationToken");
            fields["Amount"] = "abc";
            using var invalid = await client.PostAsync("/FeePayment/Collect", new FormUrlEncodedContent(fields));
            Assert.Contains("không hợp lệ", WebUtility.HtmlDecode(await invalid.Content.ReadAsStringAsync()));
            Assert.Empty(await f.Db.FeePayments.ToListAsync());
            fields["Amount"] = "3000";
            using var success = await client.PostAsync("/FeePayment/Collect", new FormUrlEncodedContent(fields));
            success.EnsureSuccessStatusCode();
            html = await success.Content.ReadAsStringAsync();
            Assert.Contains("Ghi nhận thu phí thành công", WebUtility.HtmlDecode(html));
            Assert.Contains("6.000,00", html);
            using var replay = await client.PostAsync("/FeePayment/Collect", new FormUrlEncodedContent(fields));
            Assert.Contains("đã được ghi nhận trước đó", WebUtility.HtmlDecode(await replay.Content.ReadAsStringAsync()));
            Assert.Single(await f.Db.FeePayments.ToListAsync());
            fields["RequestId"] = Input(html, "RequestId");
            fields["__RequestVerificationToken"] = Input(html, "__RequestVerificationToken");
            fields["Amount"] = "6000";
            using var full = await client.PostAsync("/FeePayment/Collect", new FormUrlEncodedContent(fields));
            Assert.Contains("Bạn đọc không có khoản nợ", WebUtility.HtmlDecode(await full.Content.ReadAsStringAsync()));
            Assert.Equal(0m, (await f.Service.GetDebtAsync(f.ReaderId))!.Balance);
            Assert.Equal(2, await f.Db.FeePayments.CountAsync());
            Assert.Equal("Nợ phí", (await f.Db.LibraryCards.AsNoTracking().SingleAsync()).Status);
        }
        finally { await app.StopAsync(); }

        static string Input(string html, string name)
        {
            var input = Regex.Match(html, $"<input[^>]*name=\"{Regex.Escape(name)}\"[^>]*>").Value;
            var value = WebUtility.HtmlDecode(Regex.Match(input, "value=\"([^\"]+)\"").Groups[1].Value);
            Assert.NotEmpty(value);
            return value;
        }
    }

    [Fact]
    public void MigrationMatchesModelAndOnlyAddsPaymentTables()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=PaymentMetadataOnly;Integrated Security=true").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var script = db.GetService<IMigrator>().GenerateScript("20261019000000_MakeDailyOverdueFeeNullableWithoutDefault", "20261020000000_AddFeePayments");
        Assert.Contains("CREATE TABLE [FeePayments]", script);
        Assert.Contains("CREATE TABLE [FeePaymentAllocations]", script);
        Assert.Contains("CREATE UNIQUE INDEX [IX_FeePayments_RequestId]", script);
        Assert.DoesNotContain("ALTER TABLE [ReaderAccounts]", script);
        Assert.DoesNotContain("ALTER TABLE [ReaderFees]", script);
        Assert.DoesNotContain("DROP TABLE", script);
    }

    [Fact]
    public async Task ViewingAndSearchingNeverChangesDebt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = fixture.Db.ChangeTracker.Entries().Count();
        Assert.Single(await fixture.Service.SearchAsync("PAY-READER"));
        Assert.Single(await fixture.Service.SearchAsync("reader@payment.test"));
        Assert.Single(await fixture.Service.SearchAsync("0900000000"));
        Assert.Single(await fixture.Service.SearchAsync("PAY-CARD"));
        Assert.Empty(await fixture.Service.SearchAsync("missing"));
        var debt = await fixture.Service.GetDebtAsync(fixture.ReaderId);
        Assert.Equal(9000m, debt!.Balance);
        Assert.True(debt.IsConsistent);
        Assert.Equal(2, debt.Fees.Count);
        Assert.Equal(before, fixture.Db.ChangeTracker.Entries().Count());
        Assert.Empty(await fixture.Db.FeePayments.ToListAsync());
    }

    [Fact]
    public async Task PartialThenFullPayment_AllocatesOldestFirst_RecordsActorAndTime()
    {
        await using var f = await Fixture.CreateAsync();
        var started = DateTime.UtcNow;
        var first = await f.Service.CollectAsync(f.Request(6000m), f.StaffId);
        Assert.True(first.IsSuccess, first.Message);
        Assert.Equal(3000m, first.BalanceAfter);
        var fees = await f.Db.ReaderFees.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(6000m, fees[0].PaidAmount); // 1000 existing + 5000 collected
        Assert.Equal(1000m, fees[1].PaidAmount);
        var saved = await f.Db.FeePayments.AsNoTracking().Include(x => x.Allocations).SingleAsync();
        Assert.Equal(f.StaffId, saved.CollectedByAdminAccountId);
        Assert.InRange(saved.PaidAtUtc, started, DateTime.UtcNow);
        Assert.Equal(6000m, saved.Allocations.Sum(a => a.Amount));
        Assert.Equal(2, saved.Allocations.Count);
        Assert.True((await f.Service.CollectAsync(f.Request(1000m), f.StaffId)).IsSuccess);
        Assert.True((await f.Service.CollectAsync(f.Request(2000m), f.StaffId)).IsSuccess);
        Assert.Equal(0m, (await f.Service.GetDebtAsync(f.ReaderId))!.Balance);
        Assert.All((await f.Service.GetDebtAsync(f.ReaderId))!.Fees, fee => Assert.Equal(0m, fee.RemainingAmount));
        Assert.Equal(9000m, (await f.Db.FeePayments.AsNoTracking().ToListAsync()).Sum(p => p.Amount));
        Assert.Equal("Nợ phí", (await f.Db.LibraryCards.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task SameTimestamp_UsesFeeIdAndRespectsWaivers()
    {
        await using var f = await Fixture.CreateAsync();
        var fees = await f.Db.ReaderFees.OrderBy(x => x.Id).ToListAsync();
        fees[1].CreatedAtUtc = fees[0].CreatedAtUtc;
        fees[0].WaivedAmount = 2000m;
        var reader = await f.Db.ReaderAccounts.SingleAsync();
        reader.OutstandingBalance -= 2000m;
        await f.Db.SaveChangesAsync();
        var result = await f.Service.CollectAsync(f.Request(4000m), f.StaffId);
        Assert.True(result.IsSuccess, result.Message);
        var allocations = await f.Db.FeePaymentAllocations.AsNoTracking().OrderBy(a => a.ReaderFeeId).ToListAsync();
        Assert.Equal(3000m, allocations[0].Amount);
        Assert.Equal(1000m, allocations[1].Amount);
        Assert.Equal(3000m, result.BalanceAfter);
    }

    [Fact]
    public async Task DuplicateReplay_IsDurableAndRejectsChangedPayload()
    {
        await using var f = await Fixture.CreateAsync();
        var request = f.Request(2000m);
        var first = await f.Service.CollectAsync(request, f.StaffId);
        await using var nextDb = new ApplicationDbContext(f.Options);
        var replay = await new FeePaymentService(nextDb).CollectAsync(request, f.StaffId);
        Assert.True(replay.IsSuccess, replay.Message);
        Assert.Equal(first.PaymentId, replay.PaymentId);
        Assert.Single(await nextDb.FeePayments.ToListAsync());
        Assert.Equal(7000m, (await nextDb.ReaderAccounts.AsNoTracking().SingleAsync()).OutstandingBalance);
        request.Amount = 3000m;
        Assert.False((await new FeePaymentService(nextDb).CollectAsync(request, f.StaffId)).IsSuccess);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("9000.01")]
    [InlineData("0.001")]
    public async Task InvalidAmounts_DoNotWrite(string input)
    {
        await using var f = await Fixture.CreateAsync();
        var amount = decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture);
        Assert.False((await f.Service.CollectAsync(f.Request(amount), f.StaffId)).IsSuccess);
        Assert.Empty(await f.Db.FeePayments.ToListAsync());
        Assert.Equal(9000m, (await f.Service.GetDebtAsync(f.ReaderId))!.Balance);
    }

    [Fact]
    public async Task MissingAmountOrConfirmationOrKey_IsRejected()
    {
        await using var f = await Fixture.CreateAsync();
        var request = f.Request(1000m);
        request.Amount = null;
        Assert.False((await f.Service.CollectAsync(request, f.StaffId)).IsSuccess);
        request.Amount = 1000m;
        request.Confirmed = false;
        Assert.False((await f.Service.CollectAsync(request, f.StaffId)).IsSuccess);
        request.Confirmed = true;
        request.RequestId = Guid.Empty;
        Assert.False((await f.Service.CollectAsync(request, f.StaffId)).IsSuccess);
        Assert.Empty(await f.Db.FeePayments.ToListAsync());
        request.RequestId = Guid.NewGuid();
        request.ReaderAccountId = int.MaxValue;
        Assert.False((await f.Service.CollectAsync(request, f.StaffId)).IsSuccess);
    }

    [Fact]
    public async Task NoDebt_OneFee_AndBalanceMismatch()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.ReaderFees.ExecuteDeleteAsync();
        await f.Db.ReaderAccounts.ExecuteUpdateAsync(u => u.SetProperty(r => r.OutstandingBalance, 0m));
        Assert.Equal(0m, (await f.Service.GetDebtAsync(f.ReaderId))!.Balance);
        Assert.False((await f.Service.CollectAsync(f.Request(1m), f.StaffId)).IsSuccess);
        f.Db.ReaderFees.Add(new ReaderFee { ReaderAccountId = f.ReaderId, Amount = 5000m });
        await f.Db.ReaderAccounts.ExecuteUpdateAsync(u => u.SetProperty(r => r.OutstandingBalance, 6000m));
        await f.Db.SaveChangesAsync();
        Assert.False((await f.Service.GetDebtAsync(f.ReaderId))!.IsConsistent);
        Assert.False((await f.Service.CollectAsync(f.Request(1m), f.StaffId)).IsSuccess);
        await f.Db.ReaderAccounts.ExecuteUpdateAsync(u => u.SetProperty(r => r.OutstandingBalance, 5000m));
        Assert.True((await f.Service.CollectAsync(f.Request(5000m), f.StaffId)).IsSuccess);
        Assert.Equal(0m, (await f.Service.GetDebtAsync(f.ReaderId))!.Balance);
    }

    [Fact]
    public async Task SaveFailure_RollsBackFeesBalanceAndTransaction()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailPayment BEFORE INSERT ON FeePayments BEGIN SELECT RAISE(ABORT, 'payment save failed'); END;");
        var request = f.Request(6000m);
        Assert.False((await f.Service.CollectAsync(request, f.StaffId)).IsSuccess);
        Assert.Equal(9000m, (await f.Service.GetDebtAsync(f.ReaderId))!.Balance);
        Assert.Empty(await f.Db.FeePayments.ToListAsync());
        Assert.Empty(await f.Db.FeePaymentAllocations.ToListAsync());
        Assert.Equal(1000m, (await f.Db.ReaderFees.AsNoTracking().OrderBy(x => x.Id).FirstAsync()).PaidAmount);
        await f.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailPayment;");
        Assert.True((await f.Service.CollectAsync(request, f.StaffId)).IsSuccess);
    }

    [Fact]
    public async Task OnlyActiveLibrarianMayCollect()
    {
        await using var f = await Fixture.CreateAsync();
        foreach (var role in new[] { AccountRoles.SystemAdmin, AccountRoles.LibraryManager })
        {
            await f.Db.AdminAccounts.ExecuteUpdateAsync(u => u.SetProperty(a => a.Role, role));
            Assert.False((await f.Service.CollectAsync(f.Request(1m), f.StaffId)).IsSuccess);
        }
        await f.Db.AdminAccounts.ExecuteUpdateAsync(u => u.SetProperty(a => a.Role, AccountRoles.Librarian).SetProperty(a => a.IsActive, false));
        Assert.False((await f.Service.CollectAsync(f.Request(1m), f.StaffId)).IsSuccess);
        Assert.False((await f.Service.CollectAsync(f.Request(1m), 0)).IsSuccess);
        Assert.Empty(await f.Db.FeePayments.ToListAsync());
        Assert.Equal(new[] { AccountRoles.Librarian }, typeof(FeePaymentController).GetCustomAttribute<StaffOnlyAttribute>()!.Roles);
        Assert.NotNull(typeof(FeePaymentController).GetMethod("Collect")!.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }

    [Fact]
    public async Task MalformedAmountModelState_DoesNotInvokeCollection()
    {
        await using var f = await Fixture.CreateAsync();
        var token = Guid.NewGuid().ToString();
        f.Db.RefreshTokens.Add(new RefreshToken
        {
            AdminAccountId = f.StaffId, TokenHash = TokenService.HashRefreshToken(token),
            CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        });
        await f.Db.SaveChangesAsync();
        var audit = new AuditLogService(f.Db, NullLogger<AuditLogService>.Instance);
        var controller = new FeePaymentController(f.Service, audit)
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers.Cookie = $"admin_refresh={token}";
        controller.ModelState.AddModelError("Amount", "The value 'abc' is not valid.");
        Assert.IsType<ViewResult>(await controller.Collect(f.Request(1000m)));
        Assert.Empty(await f.Db.FeePayments.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentRequests_CannotOverdrawOrDuplicate(bool sameRequest)
    {
        // Two independent connections to a disposable file; never connects to application data.
        var path = Path.Combine(Path.GetTempPath(), $"fee-payment-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={path};Default Timeout=2;Pooling=False").Options;
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
            var arrived = 0;
            async Task<FeePaymentResult> SubmitAsync()
            {
                await using var db = new ApplicationDbContext(options);
                if (Interlocked.Increment(ref arrived) == 2) ready.SetResult();
                await ready.Task;
                return await new FeePaymentService(db).CollectAsync(new()
                {
                    ReaderAccountId = readerId, Amount = 6000m, Confirmed = true,
                    RequestId = sameRequest ? key : Guid.NewGuid()
                }, staffId);
            }
            var results = await Task.WhenAll(Task.Run(SubmitAsync), Task.Run(SubmitAsync));
            await using var verify = new ApplicationDbContext(options);
            var payments = await verify.FeePayments.AsNoTracking().ToListAsync();
            Assert.Single(payments);
            Assert.Contains(results, r => r.IsSuccess);
            Assert.Equal(3000m, (await verify.ReaderAccounts.AsNoTracking().SingleAsync()).OutstandingBalance);
            Assert.Equal(6000m, (await verify.FeePaymentAllocations.AsNoTracking().ToListAsync()).Sum(a => a.Amount));
            Assert.Equal(3000m, (await verify.ReaderFees.AsNoTracking().ToListAsync()).Sum(f => f.RemainingAmount));
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
        public CollectFeeViewModel Request(decimal amount) => new() { ReaderAccountId = readerId, RequestId = Guid.NewGuid(), Amount = amount, Confirmed = true };

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

        public static async Task<(int ReaderId, int StaffId)> SeedAsync(ApplicationDbContext db)
        {
            var reader = new ReaderAccount
            {
                FullName = "Payment Reader", Email = "reader@payment.test", PhoneNumber = "0900000000",
                StudentOrStaffCode = "PAY-READER", PasswordHash = "test", DateOfBirth = new(2000, 1, 1), OutstandingBalance = 9000m
            };
            var staff = new AdminAccount { Email = "librarian@payment.test", PasswordHash = "test", Role = AccountRoles.Librarian };
            db.AddRange(reader, staff);
            db.LibraryCards.Add(new LibraryCard
            {
                ReaderAccount = reader, CardCode = "PAY-CARD", LibraryCardType = new() { Name = "Payment test" },
                IssuedOn = new(2026, 1, 1), ExpiresOn = new(2027, 1, 1), Status = "Nợ phí"
            });
            db.ReaderFees.AddRange(
                new ReaderFee { ReaderAccount = reader, Amount = 6000m, PaidAmount = 1000m, CreatedAtUtc = DateTime.UtcNow.AddDays(-2) },
                new ReaderFee { ReaderAccount = reader, Amount = 4000m, CreatedAtUtc = DateTime.UtcNow.AddDays(-1) });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return (reader.Id, staff.Id);
        }

        public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
