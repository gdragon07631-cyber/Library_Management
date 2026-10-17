using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

[Route("reports")]
public sealed class ReportController(ILibraryReportService reports, ApplicationDbContext db) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (!await IsManagerAsync(cancellationToken)) return RedirectToAction("Login", "Account", new { returnUrl = Url.Action(nameof(Index)) });
        var today = GetVietnamToday();
        return View(new ReportPageViewModel { StartDate = today.AddDays(-29), EndDate = today });
    }

    [HttpGet("api/loans/summary")]
    public async Task<IActionResult> Summary(DateOnly? startDate, DateOnly? endDate, CancellationToken cancellationToken)
    {
        if (!await IsManagerAsync(cancellationToken)) return Unauthorized();
        if (!TryGetRange(startDate, endDate, out var start, out var end)) return BadRequest(new { message = "Khoảng ngày không hợp lệ (tối đa 366 ngày)." });
        return Ok(await reports.GetSummaryAsync(start, end, GetVietnamToday(), cancellationToken));
    }

    [HttpGet("api/loans/top-books")]
    public async Task<IActionResult> TopBorrowedBooks(DateOnly? startDate, DateOnly? endDate, CancellationToken cancellationToken)
    {
        if (!await IsManagerAsync(cancellationToken)) return Unauthorized();
        if (!TryGetRange(startDate, endDate, out var start, out var end)) return BadRequest(new { message = "Khoảng ngày không hợp lệ (tối đa 366 ngày)." });
        return Ok(await reports.GetTopBorrowedBooksAsync(start, end, cancellationToken));
    }

    [HttpGet("api/holds/top-books")]
    public async Task<IActionResult> TopHeldBooks(CancellationToken cancellationToken)
    {
        if (!await IsManagerAsync(cancellationToken)) return Unauthorized();
        return Ok(await reports.GetTopHeldBooksAsync(cancellationToken));
    }

    [HttpGet("api/loans/daily")]
    public async Task<IActionResult> DailyLoans(DateOnly? startDate, DateOnly? endDate, CancellationToken cancellationToken)
    {
        if (!await IsManagerAsync(cancellationToken)) return Unauthorized();
        if (!TryGetRange(startDate, endDate, out var start, out var end)) return BadRequest(new { message = "Khoảng ngày không hợp lệ (tối đa 366 ngày)." });
        return Ok(await reports.GetDailyLoansAsync(start, end, cancellationToken));
    }

    private async Task<bool> IsManagerAsync(CancellationToken cancellationToken)
    {
        if (!Request.Cookies.TryGetValue("admin_refresh", out var token) || string.IsNullOrWhiteSpace(token)) return false;
        var hash = TokenService.HashRefreshToken(token);
        return await db.RefreshTokens.AsNoTracking().AnyAsync(item => item.TokenHash == hash && item.RevokedAtUtc == null &&
            item.ExpiresAtUtc > DateTime.UtcNow && item.AdminAccount.IsActive &&
            (item.AdminAccount.Role == AccountRoles.SystemAdmin || item.AdminAccount.Role == AccountRoles.LibraryManager), cancellationToken);
    }

    private static bool TryGetRange(DateOnly? requestedStart, DateOnly? requestedEnd, out DateOnly start, out DateOnly end)
    {
        var today = GetVietnamToday();
        end = requestedEnd ?? today;
        start = requestedStart ?? end.AddDays(-29);
        return start <= end && end.DayNumber - start.DayNumber < 366;
    }

    private static DateOnly GetVietnamToday()
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"); }
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
    }
}
