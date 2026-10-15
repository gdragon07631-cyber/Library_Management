using Microsoft.AspNetCore.Mvc;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

[StaffOnly(AccountRoles.Librarian)]
public sealed class FeeWaiverController(IFeeWaiverService waivers, IAuditLogService auditLogService) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken = default)
    {
        var staff = await auditLogService.GetSignedInStaffAsync(Request, cancellationToken);
        if (staff is null) return Unauthorized();
        return View(new FeeWaiverLibrarianIndexViewModel
        {
            EligibleReceivables = await waivers.GetEligibleReceivablesAsync(cancellationToken),
            Requests = await waivers.GetRequestsByLibrarianAsync(staff.Id, cancellationToken)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateFeeWaiverRequestViewModel model, CancellationToken cancellationToken = default)
    {
        if (ModelState.IsValid)
        {
            var staff = await auditLogService.GetSignedInStaffAsync(Request, cancellationToken);
            if (staff is null) return Unauthorized();
            var outcome = await waivers.CreateRequestAsync(model.ReaderFeeId, model.RequestedAmount,
                model.Reason, staff.Id, cancellationToken);
            if (outcome.IsSuccess)
            {
                TempData["SuccessMessage"] = "Đã gửi đề nghị miễn giảm. Số dư khoản phải thu chưa thay đổi.";
                return RedirectToAction(nameof(Index));
            }
            ModelState.AddModelError(string.Empty, outcome.ErrorMessage ?? "Không thể tạo đề nghị.");
        }

        var currentStaff = await auditLogService.GetSignedInStaffAsync(Request, cancellationToken);
        if (currentStaff is null) return Unauthorized();
        return View("Index", new FeeWaiverLibrarianIndexViewModel
        {
            EligibleReceivables = await waivers.GetEligibleReceivablesAsync(cancellationToken),
            Requests = await waivers.GetRequestsByLibrarianAsync(currentStaff.Id, cancellationToken),
            NewRequest = model
        });
    }
}
