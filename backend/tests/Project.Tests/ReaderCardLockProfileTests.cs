using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
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

/// <summary>S4-04 lát 3: bạn đọc xem trạng thái, lý do và thời hạn khoá thẻ của chính mình trên trang cá nhân.</summary>
public sealed class ReaderCardLockProfileTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;
    private readonly EphemeralDataProtectionProvider dataProtection = new();
    private readonly ReaderAccountAdminService adminService;
    private int sequence;

    public ReaderCardLockProfileTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        adminService = new ReaderAccountAdminService(db);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    [Fact]
    public async Task ActiveCardShowsActiveStatusOnProfileAndApi()
    {
        var reader = await AddReaderWithCardAsync();

        var profile = await ProfileAsync(reader);
        var card = await CardApiAsync(reader);

        Assert.Equal("Đang hoạt động", profile.CardStatus);
        Assert.Equal("CARD-P-1", card.GetProperty("code").GetString());
        Assert.Equal("Đang hoạt động", card.GetProperty("status").GetString());
        Assert.False(card.GetProperty("isLocked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, card.GetProperty("lockReason").ValueKind);
    }

    [Fact]
    public async Task ManualLockShowsSavedReasonAndNoExpiry()
    {
        var reader = await AddReaderWithCardAsync();
        var adminId = await AddManagerAsync();
        Assert.True((await adminService.SetCardLockedAsync(reader.Id, "Làm mất sách không báo", null, true, adminId)).IsSuccess);

        var profile = await ProfileAsync(reader);
        var card = await CardApiAsync(reader);

        Assert.Equal("Đang bị khoá", profile.CardStatus);
        Assert.Equal("Làm mất sách không báo", profile.LibraryCard!.LockReason);
        Assert.True(card.GetProperty("isLocked").GetBoolean());
        Assert.True(card.GetProperty("isLockUntilManualUnlock").GetBoolean());
        Assert.Equal(JsonValueKind.Null, card.GetProperty("lockExpiresAtUtc").ValueKind);
        Assert.Equal("Làm mất sách không báo", card.GetProperty("lockReason").GetString());
        Assert.Contains("mở khoá thủ công", card.GetProperty("lockMessage").GetString());
        // Không lộ thông tin quản trị (người thực hiện khoá) ra trang cá nhân.
        Assert.False(card.TryGetProperty("lockedByAdminAccountId", out _));
    }

    [Fact]
    public async Task LockConfirmedFromProposalShowsBothStoredReasonsAndExactTimes()
    {
        var reader = await AddReaderWithCardAsync();
        var adminId = await AddManagerAsync();
        var expiresUtc = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(10).Date.AddHours(3).AddMinutes(30), DateTimeKind.Utc);
        const string reason = "Quá hạn trả sách trên 30 ngày; Nợ phí vượt ngưỡng";
        Assert.True((await adminService.SetCardLockedAsync(reader.Id, reason, expiresUtc, false, adminId)).IsSuccess);
        var lockedAtUtc = (await db.LibraryCards.AsNoTracking().SingleAsync()).LockedAtUtc!.Value;

        var card = await CardApiAsync(reader);

        Assert.Equal(reason, card.GetProperty("lockReason").GetString());
        Assert.False(card.GetProperty("isLockUntilManualUnlock").GetBoolean());
        var apiExpiry = card.GetProperty("lockExpiresAtUtc").GetDateTime();
        Assert.Equal(DateTimeKind.Utc, apiExpiry.Kind);
        Assert.Equal(expiresUtc, apiExpiry);
        Assert.EndsWith("Z", card.GetProperty("lockedAtUtc").GetString());
        Assert.Equal(DateTime.SpecifyKind(lockedAtUtc, DateTimeKind.Utc), card.GetProperty("lockedAtUtc").GetDateTime());
        // Thông báo hiển thị theo giờ thư viện, khớp với ngày giờ Quản lý đã chọn.
        Assert.Contains($"Hạn khoá: {expiresUtc.ToLocalTime():dd/MM/yyyy HH:mm}", card.GetProperty("lockMessage").GetString());
    }

    [Fact]
    public async Task OldLockedDataWithoutReasonIsStillShownAsLocked()
    {
        var reader = await AddReaderWithCardAsync();
        var stored = await db.LibraryCards.SingleAsync();
        stored.Status = "Đang bị khoá";
        await db.SaveChangesAsync();

        var profile = await ProfileAsync(reader);
        var card = await CardApiAsync(reader);

        Assert.Equal("Đang bị khoá", profile.CardStatus);
        Assert.True(card.GetProperty("isLocked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, card.GetProperty("lockReason").ValueKind);
    }

    [Fact]
    public async Task ExpiredTimedLockShowsActiveButKeepsLockInformation()
    {
        var reader = await AddReaderWithCardAsync();
        var stored = await db.LibraryCards.SingleAsync();
        stored.Status = "Đang bị khoá";
        stored.LockReason = "Vi phạm nội quy";
        stored.LockedAtUtc = DateTime.UtcNow.AddDays(-5);
        stored.LockExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        var profile = await ProfileAsync(reader);
        var card = await CardApiAsync(reader);

        Assert.Equal("Đang hoạt động", profile.CardStatus);
        Assert.False(card.GetProperty("isLocked").GetBoolean());
        Assert.Equal("Vi phạm nội quy", card.GetProperty("lockReason").GetString());
        db.ChangeTracker.Clear();
        var afterView = await db.LibraryCards.AsNoTracking().SingleAsync();
        Assert.Equal("Vi phạm nội quy", afterView.LockReason);
        Assert.NotNull(afterView.LockedAtUtc);
    }

    [Fact]
    public async Task ManualLockIsNeverConsideredExpired()
    {
        var reader = await AddReaderWithCardAsync();
        var stored = await db.LibraryCards.SingleAsync();
        stored.Status = "Đang bị khoá";
        stored.LockReason = "Khoá thủ công";
        stored.LockedAtUtc = DateTime.UtcNow.AddYears(-2);
        stored.IsLockUntilManualUnlock = true;
        await db.SaveChangesAsync();

        Assert.Equal("Đang bị khoá", (await ProfileAsync(reader)).CardStatus);
    }

    [Fact]
    public async Task ProfileReflectsNewLockAfterReload()
    {
        var reader = await AddReaderWithCardAsync();
        var adminId = await AddManagerAsync();
        Assert.Equal("Đang hoạt động", (await ProfileAsync(reader)).CardStatus);

        Assert.True((await adminService.SetCardLockedAsync(reader.Id, "Vi phạm", DateTime.UtcNow.AddDays(3), false, adminId)).IsSuccess);
        db.ChangeTracker.Clear();

        Assert.Equal("Đang bị khoá", (await ProfileAsync(reader)).CardStatus);
        Assert.True((await CardApiAsync(reader)).GetProperty("isLocked").GetBoolean());
    }

    [Fact]
    public async Task ReaderCannotChangeLockDataThroughProfileForm()
    {
        var reader = await AddReaderWithCardAsync();
        var adminId = await AddManagerAsync();
        Assert.True((await adminService.SetCardLockedAsync(reader.Id, "Lý do gốc", null, true, adminId)).IsSuccess);
        db.ChangeTracker.Clear();

        var tampered = new ReaderProfileViewModel
        {
            PhoneNumber = reader.PhoneNumber, Address = "Địa chỉ mới", Email = reader.Email,
            CardStatus = "Đang hoạt động",
            LibraryCard = new LibraryCard { Status = "Đang hoạt động", LockReason = null, IsLockUntilManualUnlock = false }
        };
        await ReaderController(SessionCookie(reader)).Profile(tampered);

        db.ChangeTracker.Clear();
        var card = await db.LibraryCards.AsNoTracking().SingleAsync();
        Assert.Equal("Đang bị khoá", card.Status);
        Assert.Equal("Lý do gốc", card.LockReason);
        Assert.True(card.IsLockUntilManualUnlock);
    }

    [Fact]
    public async Task ForgedReaderIdCookieWithoutSessionStampIsRejected()
    {
        var me = await AddReaderWithCardAsync();
        var other = await AddReaderWithCardAsync();
        var adminId = await AddManagerAsync();
        Assert.True((await adminService.SetCardLockedAsync(other.Id, "Thông tin riêng của người khác", null, true, adminId)).IsSuccess);

        var forged = $"reader_id={other.Id}";
        Assert.IsType<UnauthorizedObjectResult>(await ReaderController(forged).GetProfileApi());
        Assert.Equal(nameof(ReaderRegistrationController.Login),
            Assert.IsType<RedirectToActionResult>(await ReaderController(forged).Profile((int?)null)).ActionName);

        // Phiên thật của mình kèm reader_id của người khác cũng bị từ chối vì chữ ký không khớp.
        var mixed = SessionCookie(me).Replace($"reader_id={me.Id}", $"reader_id={other.Id}");
        Assert.IsType<UnauthorizedObjectResult>(await ReaderController(mixed).GetProfileApi());

        var mine = await CardApiAsync(me);
        Assert.Equal("CARD-P-1", mine.GetProperty("code").GetString());
        Assert.False(mine.GetProperty("isLocked").GetBoolean());
    }

    [Fact]
    public async Task ReaderWithoutCardOrUnknownSessionIsHandled()
    {
        var reader = await AddReaderWithCardAsync(withCard: false);

        var profile = await ProfileAsync(reader);
        var card = await CardApiAsync(reader);

        Assert.Null(profile.CardCode);
        Assert.Equal("Chưa có thẻ", profile.CardStatus);
        Assert.Equal("Chưa có thẻ", card.GetProperty("status").GetString());
        Assert.False(card.GetProperty("isLocked").GetBoolean());

        Assert.IsType<UnauthorizedObjectResult>(await ReaderController(cookie: null).GetProfileApi());
        var cookie = SessionCookie(reader);
        db.ReaderAccounts.Remove(await db.ReaderAccounts.SingleAsync());
        await db.SaveChangesAsync();
        Assert.IsType<UnauthorizedObjectResult>(await ReaderController(cookie).GetProfileApi());
    }

    [Fact]
    public async Task ProfileStatusMatchesHoldCheckForLockedCard()
    {
        var reader = await AddReaderWithCardAsync();
        var adminId = await AddManagerAsync();
        var book = new Book { Title = "Sách cần đặt giữ", Author = new Author { Name = "Tác giả" } };
        db.Books.Add(book);
        await db.SaveChangesAsync();
        Assert.True((await adminService.SetCardLockedAsync(reader.Id, "Nợ phí vượt ngưỡng", null, true, adminId)).IsSuccess);
        db.ChangeTracker.Clear();

        var profile = await ProfileAsync(reader);
        var hold = await ReaderController(SessionCookie(reader)).HoldDocumentApi(book.Id);

        Assert.Equal("Đang bị khoá", profile.CardStatus);
        var rejected = Assert.IsType<ObjectResult>(hold);
        Assert.Equal(StatusCodes.Status403Forbidden, rejected.StatusCode);
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(rejected.Value));
        Assert.Contains("Nợ phí vượt ngưỡng", body.RootElement.GetProperty("message").GetString());
        Assert.Empty(await db.BookHolds.ToListAsync());
    }

    // ---------- Hỗ trợ ----------

    private async Task<ReaderProfileViewModel> ProfileAsync(ReaderAccount reader)
    {
        db.ChangeTracker.Clear();
        var result = await ReaderController(SessionCookie(reader)).Profile((int?)null);
        return Assert.IsType<ReaderProfileViewModel>(Assert.IsType<ViewResult>(result).Model);
    }

    private async Task<JsonElement> CardApiAsync(ReaderAccount reader)
    {
        db.ChangeTracker.Clear();
        var result = Assert.IsType<OkObjectResult>(await ReaderController(SessionCookie(reader)).GetProfileApi());
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return document.RootElement.GetProperty("libraryCard").Clone();
    }

    private async Task<ReaderAccount> AddReaderWithCardAsync(bool withCard = true)
    {
        var index = ++sequence;
        var reader = new ReaderAccount
        {
            FullName = $"Bạn đọc {index}", DateOfBirth = new DateOnly(2000, 1, 1), Email = $"profile{index}@example.test",
            PhoneNumber = $"09110000{index:00}", StudentOrStaffCode = $"BD-P-{index}", Address = "Hà Nội",
            Status = "Đang hoạt động", EmailConfirmed = true
        };
        reader.PasswordHash = new PasswordHasher<ReaderAccount>().HashPassword(reader, "password123");
        if (withCard)
        {
            reader.LibraryCard = new LibraryCard
            {
                CardCode = $"CARD-P-{index}",
                LibraryCardType = new LibraryCardType { Name = $"Thẻ thường {index}", MaxRenewals = 2 },
                IssuedOn = DateOnly.FromDateTime(DateTime.Today.AddYears(-1)),
                ExpiresOn = DateOnly.FromDateTime(DateTime.Today.AddYears(1)),
                Status = "Đang hoạt động"
            };
        }
        db.ReaderAccounts.Add(reader);
        await db.SaveChangesAsync();
        return reader;
    }

    private async Task<int> AddManagerAsync()
    {
        var existing = await db.AdminAccounts.FirstOrDefaultAsync();
        if (existing is not null) return existing.Id;
        var admin = new AdminAccount { Email = "manager@library.test", Role = AccountRoles.LibraryManager, IsActive = true, PasswordHash = "hash" };
        db.AdminAccounts.Add(admin);
        await db.SaveChangesAsync();
        return admin.Id;
    }

    private string SessionCookie(ReaderAccount reader)
    {
        var context = new DefaultHttpContext();
        ReaderSessionCookies.Append(context, dataProtection, reader);
        return string.Join("; ", SetCookieHeaderValue.ParseList(context.Response.Headers.SetCookie.Select(value => value!).ToList())
            .Select(cookie => $"{cookie.Name}={cookie.Value}"));
    }

    private ReaderRegistrationController ReaderController(string? cookie)
    {
        var registration = new ReaderRegistrationService(db, new PasswordHasher<ReaderAccount>(), NullLogger<ReaderRegistrationService>.Instance);
        var controller = new ReaderRegistrationController(registration, new ReaderRegistrationIpRateLimiter(),
            new ReaderPasswordResetService(db, new PasswordHasher<ReaderAccount>(), new NoEmailSender(), NullLogger<ReaderPasswordResetService>.Instance),
            dataProtection, new AuditLogService(db, NullLogger<AuditLogService>.Instance), new NoOpEmailVerificationService());
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
