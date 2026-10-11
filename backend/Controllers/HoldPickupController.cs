using Microsoft.AspNetCore.Mvc;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

/// <summary>
/// Màn hình thủ thư: danh sách sách đang chờ người đến nhận (giá chờ).
/// Read-only: hiển thị mã vạch bản sao, tên bạn đọc, hạn nhận (sort theo hạn gần nhất).
/// </summary>
[StaffOnly(AccountRoles.Librarian, AccountRoles.SystemAdmin, AccountRoles.LibraryManager)]
public sealed class HoldPickupController(
    IHoldPickupService holdPickupService,
    IHoldPickupConfirmationService confirmationService,
    IHoldPickupExpiryService expiryService,
    IAuditLogService auditLogService,
    ILogger<HoldPickupController> logger) : Controller
{
    // ==========================================
    // MVC VIEW ACTION
    // ==========================================

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var items = await holdPickupService.GetWaitingPickupHoldsAsync(ct);
        var staff = await auditLogService.GetSignedInStaffAsync(Request, ct);
        return View(new HoldPickupListViewModel
        {
            Items = items,
            CanProcessOverdue = staff?.Role is AccountRoles.LibraryManager or AccountRoles.SystemAdmin
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ProcessExpired(CancellationToken ct = default)
    {
        var staff = await auditLogService.GetSignedInStaffAsync(Request, ct);
        if (staff is null) return Unauthorized();
        if (staff.Role != AccountRoles.LibraryManager && staff.Role != AccountRoles.SystemAdmin) return Forbid();

        try
        {
            var result = await expiryService.ProcessOverdueAsync(DateTime.UtcNow, ct);
            TempData[result.Errors.Count == 0 ? "SuccessMessage" : "ErrorMessage"] = result.Errors.Count > 0
                ? $"Đã xử lý {result.CancelledCount}/{result.CheckedCount} đơn quá hạn; có {result.Errors.Count} lỗi."
                : $"Đã kiểm tra {result.CheckedCount} đơn; hủy {result.CancelledCount}, chuyển {result.AssignedToNextReaderCount} bản sao cho người tiếp theo, trả {result.ReturnedToAvailableCount} bản về trạng thái sẵn sàng.";
            return RedirectToAction(nameof(Index));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Failed to manually process overdue book holds.");
            TempData["ErrorMessage"] = "Không thể xử lý đơn quá hạn do lỗi hệ thống. Vui lòng thử lại.";
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet]
    public async Task<IActionResult> Details(long id, CancellationToken ct = default)
    {
        var item = await holdPickupService.GetHoldAsync(id, ct);
        return item == null ? NotFound() : View(item);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(ConfirmHoldPickupViewModel model, CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = ModelState.Values.SelectMany(value => value.Errors).FirstOrDefault()?.ErrorMessage
                ?? "Vui lòng nhập mã thẻ bạn đọc.";
            return RedirectToAction(nameof(Details), new { id = model.HoldId });
        }

        try
        {
            var staff = await auditLogService.GetSignedInStaffAsync(Request, ct);
            if (staff is null) return Unauthorized();
            var result = await confirmationService.ConfirmAsync(model.HoldId, model.LibraryCardCode, staff.Email, staff.Id, ct);
            TempData[result.IsSuccess ? "SuccessMessage" : "ErrorMessage"] = result.Message;
            return result.IsSuccess && result.LoanId is long loanId
                ? RedirectToAction("Details", "BookLoanDetails", new { id = loanId })
                : RedirectToAction(nameof(Details), new { id = model.HoldId });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Failed to confirm book hold {HoldId}.", model.HoldId);
            TempData["ErrorMessage"] = "Không thể hoàn tất xác nhận do lỗi hệ thống. Vui lòng tải lại trang để kiểm tra trạng thái đơn.";
            return RedirectToAction(nameof(Index));
        }
    }

    // ==========================================
    // REST API ENDPOINT
    // ==========================================

    [HttpGet("api/hold-pickups")]
    public async Task<IActionResult> GetAllApi(CancellationToken ct = default)
    {
        var items = await holdPickupService.GetWaitingPickupHoldsAsync(ct);
        return Ok(items.Select(h => new
        {
            holdId = h.HoldId,
            bookId = h.BookId,
            bookTitle = h.BookTitle,
            isbn = h.Isbn,
            bookCopyId = h.BookCopyId,
            copyBarcode = h.CopyBarcode,
            readerAccountId = h.ReaderAccountId,
            readerName = h.ReaderName,
            readerEmail = h.ReaderEmail,
            readerPhone = h.ReaderPhone,
            pickupDeadlineUtc = h.PickupDeadlineUtc,
            heldAtUtc = h.HeldAtUtc,
            status = h.Status,
            currentShelf = h.CurrentShelf,
            currentWarehouse = h.CurrentWarehouse
        }));
    }
}

