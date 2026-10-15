using Microsoft.AspNetCore.Mvc;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

[StaffOnly(AccountRoles.LibraryManager)]
public sealed class FeeWaiverReviewController(IFeeWaiverService waivers, IAuditLogService auditLogService) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken = default) =>
        View(new FeeWaiverManagerIndexViewModel
        {
            PendingRequests = await waivers.GetPendingRequestsAsync(cancellationToken)
        });

    [HttpGet]
    public async Task<IActionResult> Details(long id, CancellationToken cancellationToken = default)
    {
        var request = await waivers.GetRequestAsync(id, cancellationToken);
        return request is null ? NotFound() : View(request);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Approve(long id, FeeWaiverDecisionViewModel model, CancellationToken cancellationToken = default) =>
        DecideAsync(id, approve: true, model, cancellationToken);

    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Reject(long id, FeeWaiverDecisionViewModel model, CancellationToken cancellationToken = default) =>
        DecideAsync(id, approve: false, model, cancellationToken);

    private async Task<IActionResult> DecideAsync(
        long id, bool approve, FeeWaiverDecisionViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = "Ghi chú xử lý không hợp lệ.";
            return RedirectToAction(nameof(Details), new { id });
        }
        var staff = await auditLogService.GetSignedInStaffAsync(Request, cancellationToken);
        if (staff is null) return Unauthorized();
        var outcome = await waivers.DecideAsync(id, approve, model.ReviewNote, staff.Id, cancellationToken);
        if (!outcome.IsSuccess)
        {
            TempData["ErrorMessage"] = outcome.ErrorMessage ?? "Không thể xử lý đề nghị.";
            return RedirectToAction(nameof(Details), new { id });
        }

        TempData["SuccessMessage"] = approve
            ? "Đã duyệt đề nghị. Số dư phải thu được cập nhật và phí gốc vẫn được lưu trong lịch sử."
            : "Đã từ chối đề nghị. Số dư khoản phải thu không thay đổi.";
        return RedirectToAction(nameof(Details), new { id });
    }
}
