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
        return View(reports.GetDemoReport(start, end, today));
    }

    [HttpGet("api/reports/loans")]
    public async Task<IActionResult> GetLoanReport(
        [FromQuery] string? startDate,
        [FromQuery] string? endDate,
        CancellationToken ct = default)
    {
        var access = await CheckAccessAsync(ct);
        if (access == AccessResult.Unauthenticated) return Unauthorized(new { message = "Vui lòng đăng nhập để xem báo cáo." });
        if (access == AccessResult.Forbidden) return StatusCode(StatusCodes.Status403Forbidden, new { message = "Bạn không có quyền xem báo cáo." });

        DateOnly start;
        DateOnly end;
        if (startDate is null && endDate is null)
        {
            (start, end) = DefaultRange(GetVietnamToday());
        }
        else if (!DateOnly.TryParseExact(startDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out start) ||
                 !DateOnly.TryParseExact(endDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out end))
        {
            return BadRequest(new { message = "Ngày phải đúng định dạng YYYY-MM-DD." });
        }
        else if (start > end)
        {
            return BadRequest(new { message = "Ngày bắt đầu không được sau ngày kết thúc." });
        }

        var today = GetVietnamToday();
        return Ok(reports.GetDemoReport(start, end, today));
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

    private static DateOnly GetVietnamToday() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, VietnamTimeZone));

    private static (DateOnly Start, DateOnly End) DefaultRange(DateOnly today) => (today.AddDays(-29), today);

    private enum AccessResult { Allowed, Unauthenticated, Forbidden }
}
