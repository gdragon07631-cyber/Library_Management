<<<<<<< HEAD:backend/Controllers/ReportController.cs
<<<<<<< HEAD:backend/Controllers/ReportController.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Filters;
=======
=======
>>>>>>> origin/feature/S4-07/xem_10_dau_sach_co_hang_doi_dat_giu:Controllers/ReportController.cs
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Project.Data;
<<<<<<< HEAD:backend/Controllers/ReportController.cs
>>>>>>> origin/feature/S4-07/xem_tong_quan_luot_muon_va_tinh_trang:Controllers/ReportController.cs
=======
>>>>>>> origin/feature/S4-07/xem_10_dau_sach_co_hang_doi_dat_giu:Controllers/ReportController.cs
using Project.Models;
using Project.Services;

namespace Project.Controllers;

<<<<<<< HEAD:backend/Controllers/ReportController.cs
<<<<<<< HEAD:backend/Controllers/ReportController.cs
[Route("reports")]
[StaffOnly(AccountRoles.SystemAdmin, AccountRoles.LibraryManager)]
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
=======
=======
>>>>>>> origin/feature/S4-07/xem_10_dau_sach_co_hang_doi_dat_giu:Controllers/ReportController.cs
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
<<<<<<< HEAD:backend/Controllers/ReportController.cs
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
=======
        var summary = reports.GetDemoSummary(start, end, today);
        var topBooks = await reports.GetTopBooksAsync(start, end, ct);
        var topHeldBooks = await reports.GetTopHeldBooksAsync(ct);
        return View(new LoanReportPageViewModel
        {
            Summary = summary,
            TopBooks = topBooks,
            TopHeldBooks = topHeldBooks
        });
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

    private async Task<IActionResult?> CheckApiAccessAsync(CancellationToken ct)
    {
        var access = await CheckAccessAsync(ct);
        return access switch
        {
            AccessResult.Unauthenticated => Unauthorized(new { message = "Vui lòng đăng nhập để xem báo cáo." }),
            AccessResult.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { message = "Bạn không có quyền xem báo cáo." }),
            _ => null
        };
>>>>>>> origin/feature/S4-07/xem_10_dau_sach_co_hang_doi_dat_giu:Controllers/ReportController.cs
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

<<<<<<< HEAD:backend/Controllers/ReportController.cs
=======
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

>>>>>>> origin/feature/S4-07/xem_10_dau_sach_co_hang_doi_dat_giu:Controllers/ReportController.cs
    private static DateOnly GetVietnamToday() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, VietnamTimeZone));

    private static (DateOnly Start, DateOnly End) DefaultRange(DateOnly today) => (today.AddDays(-29), today);

    private enum AccessResult { Allowed, Unauthenticated, Forbidden }
<<<<<<< HEAD:backend/Controllers/ReportController.cs
>>>>>>> origin/feature/S4-07/xem_tong_quan_luot_muon_va_tinh_trang:Controllers/ReportController.cs
=======
>>>>>>> origin/feature/S4-07/xem_10_dau_sach_co_hang_doi_dat_giu:Controllers/ReportController.cs
}
