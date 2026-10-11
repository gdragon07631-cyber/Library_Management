using Microsoft.AspNetCore.Mvc;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

[StaffOnly(AccountRoles.LibraryManager, AccountRoles.SystemAdmin)]
public sealed class HoldPickupHistoryController(
    IHoldPickupExpiryHistoryService historyService,
    TimeProvider timeProvider,
    ILogger<HoldPickupHistoryController> logger) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(int page = 1, CancellationToken cancellationToken = default)
    {
        try
        {
            return View(await historyService.GetLast30DaysAsync(
                timeProvider.GetUtcNow().UtcDateTime, page, cancellationToken: cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not load the recent automatic hold cancellation history page.");
            return View(new AutoCancelledHoldHistoryPageViewModel
            {
                Page = Math.Max(1, page),
                ErrorMessage = "Không thể tải lịch sử lúc này. Vui lòng tải lại trang."
            });
        }
    }

    [HttpGet("api/hold-pickups/auto-cancelled-history")]
    public async Task<IActionResult> GetApi(int page = 1, int pageSize = 25, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await historyService.GetLast30DaysAsync(
                timeProvider.GetUtcNow().UtcDateTime, page, pageSize, cancellationToken);
            return Ok(result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not query recent automatic hold cancellation history.");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "Không thể truy vấn lịch sử lúc này." });
        }
    }
}
