using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

[StaffOnly(AccountRoles.SystemAdmin, AccountRoles.LibraryManager)]
[Route("reports")]
public sealed class ReportController(ILibraryReportService reports, ApplicationDbContext db) : Controller
{
    [HttpGet(""), HttpGet("/Report")]
    public async Task<IActionResult> Index()
    {
        if (!await IsReportStaffAsync()) return RedirectToAction("Login", "Auth");
        var today = GetHoChiMinhToday();
        return View(new ReportPageViewModel { StartDate = today.AddDays(-29), EndDate = today });
    }

    [HttpGet("api/loans/summary"), HttpGet("/api/reports/loans/summary")]
    public async Task<IActionResult> Summary([FromQuery] string? startDate, [FromQuery] string? endDate, CancellationToken cancellationToken)
    {
        if (!await IsReportStaffAsync()) return Unauthorized(new { message = "Bạn không có quyền xem báo cáo." });
        var today = GetHoChiMinhToday();
        if (!TryGetRange(startDate, endDate, today, out var start, out var end))
            return BadRequest(new { message = "Khoảng ngày không hợp lệ." });
        return Ok(await reports.GetSummaryAsync(start, end, today, cancellationToken));
    }

    [HttpGet("api/loans/top-books"), HttpGet("/api/reports/loans/top-books")]
    public async Task<IActionResult> TopBorrowedBooks([FromQuery] string? startDate, [FromQuery] string? endDate, CancellationToken cancellationToken)
    {
        if (!await IsReportStaffAsync()) return Unauthorized(new { message = "Bạn không có quyền xem báo cáo." });
        var today = GetHoChiMinhToday();
        if (!TryGetRange(startDate, endDate, today, out var start, out var end))
            return BadRequest(new { message = "Khoảng ngày không hợp lệ." });
        return Ok(await reports.GetTopBorrowedBooksAsync(start, end, cancellationToken));
    }

    [HttpGet("api/holds/top-books"), HttpGet("/api/reports/holds/top-books")]
    public async Task<IActionResult> TopHeldBooks(CancellationToken cancellationToken)
    {
        if (!await IsReportStaffAsync()) return Unauthorized(new { message = "Bạn không có quyền xem báo cáo." });
        return Ok(await reports.GetTopHeldBooksAsync(cancellationToken));
    }

    [HttpGet("api/loans/daily"), HttpGet("/api/reports/loans/daily")]
    public async Task<IActionResult> DailyLoans([FromQuery] string? startDate, [FromQuery] string? endDate, CancellationToken cancellationToken)
    {
        if (!await IsReportStaffAsync()) return Unauthorized(new { message = "Bạn không có quyền xem báo cáo." });
        var today = GetHoChiMinhToday();
        if (!TryGetRange(startDate, endDate, today, out var start, out var end))
            return BadRequest(new { message = "Khoảng ngày không hợp lệ." });
        return Ok(await reports.GetDailyLoansAsync(start, end, cancellationToken));
    }

    private static bool TryGetRange(string? startDate, string? endDate, DateOnly today, out DateOnly start, out DateOnly end)
    {
        if (string.IsNullOrWhiteSpace(startDate) && string.IsNullOrWhiteSpace(endDate))
        {
            end = today;
            start = today.AddDays(-29);
            return true;
        }
        var validStart = DateOnly.TryParseExact(startDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out start);
        var validEnd = DateOnly.TryParseExact(endDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out end);
        return validStart && validEnd && start <= end;
    }

    private static DateOnly GetHoChiMinhToday()
    {
        foreach (var id in new[] { "Asia/Ho_Chi_Minh", "SE Asia Standard Time" })
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
                return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
            }
            catch (TimeZoneNotFoundException) { }
        }
        return DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
    }

    private async Task<bool> IsReportStaffAsync()
    {
        if (!Request.Cookies.TryGetValue("admin_refresh", out var refreshToken) || string.IsNullOrWhiteSpace(refreshToken))
            return false;
        var hash = TokenService.HashRefreshToken(refreshToken);
        return await db.RefreshTokens.AsNoTracking().AnyAsync(token =>
            token.TokenHash == hash &&
            token.RevokedAtUtc == null &&
            token.ExpiresAtUtc > DateTime.UtcNow &&
            token.AdminAccount != null &&
            token.AdminAccount.IsActive &&
            (token.AdminAccount.Role == AccountRoles.SystemAdmin || token.AdminAccount.Role == AccountRoles.LibraryManager));
    }
}
