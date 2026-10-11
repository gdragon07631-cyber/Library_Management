using Microsoft.AspNetCore.Mvc;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

[StaffOnly(AccountRoles.Librarian, AccountRoles.LibraryManager, AccountRoles.SystemAdmin)]
public sealed class DirectLoanController(DirectLoanService service, IAuditLogService audit) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(string? cardCode, string? copyCode, long? loanId, CancellationToken ct)
    {
        var model = await service.LookupAsync(cardCode, copyCode, ct);
        if (loanId.HasValue) model.Receipt = await service.ReceiptAsync(loanId.Value, ct);
        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(DirectLoanRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View("Index", await service.LookupAsync(request.CardCode, request.CopyCode, ct));
        var staff = await audit.GetSignedInStaffAsync(Request, ct);
        if (staff == null) return Unauthorized();
        var result = await service.ConfirmAsync(request.CardCode, request.CopyCode, staff, ct, AuditLogService.ClientIp(HttpContext));
        if (!result.IsSuccess)
        {
            ModelState.AddModelError(string.Empty, result.ErrorMessage ?? "Không thể tạo phiếu mượn.");
            return View("Index", await service.LookupAsync(request.CardCode, request.CopyCode, ct));
        }
        TempData["DirectLoanSuccess"] = "Cho mượn thành công.";
        return RedirectToAction(nameof(Index), new { cardCode = request.CardCode.Trim(), copyCode = request.CopyCode.Trim(), loanId = result.Loan!.Id });
    }
}
