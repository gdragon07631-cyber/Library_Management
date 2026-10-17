using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

public sealed class ReportController(ApplicationDbContext db, ILoanReportService reports) : Controller
{
    private static readonly TimeZoneInfo VietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var access = await CheckAccessAsync(ct);
        if (access != AccessResult.Allowed)
            return access == AccessResult.Unauthenticated
                ? RedirectToAction("Login", "Account", new { returnUrl = Url.Action(nameof(Index)) })
                : Forbid();

        var today = GetVietnamToday();
        var (start, end) = DefaultRange(today);
        var page = new LoanReportPageViewModel
        {
            Summary = reports.GetDemoSummary(start, end, today),
            TopBooks = await reports.GetTopBooksAsync(start, end, ct),
            TopHeldBooks = await reports.GetTopHeldBooksAsync(ct),
            DailyLoans = await reports.GetDailyLoansAsync(start, end, ct)
        };
        return View(page);
    }

    [HttpGet("api/reports/loans/summary")]
    public async Task<IActionResult> GetSummary(
        [FromQuery] string? startDate, [FromQuery] string? endDate, CancellationToken ct = default)
    {
        var access = await CheckApiAccessAsync(ct);
        if (access is not null) return access;
        if (!TryResolveRange(startDate, endDate, out var start, out var end, out var error))
            return BadRequest(new { message = error });
        return Ok(reports.GetDemoSummary(start, end, GetVietnamToday()));
    }

    [HttpGet("api/reports/loans/top-books")]
    public async Task<IActionResult> GetTopBooks(
        [FromQuery] string? startDate, [FromQuery] string? endDate, CancellationToken ct = default)
    {
        var access = await CheckApiAccessAsync(ct);
        if (access is not null) return access;
        if (!TryResolveRange(startDate, endDate, out var start, out var end, out var error))
            return BadRequest(new { message = error });
        return Ok(await reports.GetTopBooksAsync(start, end, ct));
    }

    [HttpGet("api/reports/holds/top-books")]
    public async Task<IActionResult> GetTopHeldBooks(CancellationToken ct = default)
    {
        var access = await CheckApiAccessAsync(ct);
        if (access is not null) return access;
        return Ok(await reports.GetTopHeldBooksAsync(ct));
    }

    [HttpGet("api/reports/loans/daily")]
    public async Task<IActionResult> GetDailyLoans(
        [FromQuery] string? startDate, [FromQuery] string? endDate, CancellationToken ct = default)
    {
        var access = await CheckApiAccessAsync(ct);
        if (access is not null) return access;
        if (!TryResolveRange(startDate, endDate, out var start, out var end, out var error))
            return BadRequest(new { message = error });
        return Ok(await reports.GetDailyLoansAsync(start, end, ct));
    }

    private async Task<IActionResult?> CheckApiAccessAsync(CancellationToken ct)
    {
        var access = await CheckAccessAsync(ct);
        return access switch
        {
            AccessResult.Unauthenticated => Unauthorized(new { message = "Vui lòng đăng nhập để xem báo cáo." }),
            AccessResult.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { message = "Bạn không có quyền xem báo cáo." }),
            _ => null
        };
    }

    private async Task<AccessResult> CheckAccessAsync(CancellationToken ct)
    {
        if (!Request.Cookies.TryGetValue("admin_refresh", out var token) || string.IsNullOrWhiteSpace(token))
            return AccessResult.Unauthenticated;

        var hash = TokenService.HashRefreshToken(token);
        var role = await db.RefreshTokens.AsNoTracking()
            .Where(item => item.TokenHash == hash && item.RevokedAtUtc == null && item.ExpiresAtUtc > DateTime.UtcNow && item.AdminAccount.IsActive)
            .Select(item => item.AdminAccount.Role)
            .FirstOrDefaultAsync(ct);
        return role is null ? AccessResult.Unauthenticated
            : role is AccountRoles.SystemAdmin or AccountRoles.LibraryManager ? AccessResult.Allowed
            : AccessResult.Forbidden;
    }

    private static bool TryResolveRange(
        string? startDate, string? endDate, out DateOnly start, out DateOnly end, out string? error)
    {
        error = null;
        if (startDate is null && endDate is null)
        {
            (start, end) = DefaultRange(GetVietnamToday());
            return true;
        }
        if (!DateOnly.TryParseExact(startDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out start))
        {
            end = default;
            error = "Ngày phải đúng định dạng YYYY-MM-DD.";
            return false;
        }
        if (!DateOnly.TryParseExact(endDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out end))
        {
            error = "Ngày phải đúng định dạng YYYY-MM-DD.";
            return false;
        }
        if (start > end)
        {
            error = "Ngày bắt đầu không được sau ngày kết thúc.";
            return false;
        }
        return true;
    }

    private static DateOnly GetVietnamToday() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, VietnamTimeZone));

    private static (DateOnly Start, DateOnly End) DefaultRange(DateOnly today) => (today.AddDays(-29), today);

    private enum AccessResult { Allowed, Unauthenticated, Forbidden }
}
