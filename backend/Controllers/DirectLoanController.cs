using Microsoft.AspNetCore.Mvc;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

[StaffOnly(AccountRoles.Librarian, AccountRoles.LibraryManager, AccountRoles.SystemAdmin)]
public sealed class DirectLoanController(DirectLoanService service, IAuditLogService audit) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(string? cardCode, string? copyCode, long? loanId, CancellationToken ct, long[]? loanIds = null)
    {
        var model = await service.LookupAsync(cardCode, copyCode, ct);
        if (loanId.HasValue) model.Receipt = await service.ReceiptAsync(loanId.Value, ct);
        if (model.CanConfirm) model.SelectedCopies.Add(new(model.CopyCode, model.BookTitle!, model.CopyStatus!));
        foreach (var id in (loanIds ?? []).Distinct())
        {
            var receipt = await service.ReceiptAsync(id, ct);
            if (receipt != null) model.Receipts.Add(receipt);
        }
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> LookupCopy(string? cardCode, string? copyCode, CancellationToken ct)
    {
        var model = await service.LookupAsync(cardCode, copyCode, ct);
        if (!model.CanConfirm) return BadRequest(new { error = model.ReaderError ?? model.CopyError ?? "Vui lòng nhập mã thẻ và mã vạch." });
        return Json(new DirectLoanSelection(model.CopyCode, model.BookTitle!, model.CopyStatus!));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmMany(DirectLoanBatchRequest request, CancellationToken ct)
    {
        var staff = await audit.GetSignedInStaffAsync(Request, ct);
        if (staff == null) return Unauthorized();
        var result = ModelState.IsValid
            ? await service.ConfirmManyAsync(request.CardCode, request.CopyCodes, staff, ct, AuditLogService.ClientIp(HttpContext))
            : new DirectLoanBatchOutcome(false, "Thông tin mượn không hợp lệ.");
        if (result.IsSuccess)
        {
            TempData["DirectLoanSuccess"] = "Cho mượn thành công.";
            return RedirectToAction(nameof(Index), new { cardCode = request.CardCode.Trim(), loanIds = result.Loans!.Select(l => l.Id).ToArray() });
        }
        ModelState.AddModelError(string.Empty, result.ErrorMessage ?? "Không thể tạo phiếu mượn.");
        var model = await service.LookupAsync(request.CardCode, null, ct);
        foreach (var code in request.CopyCodes)
        {
            var copy = await service.LookupAsync(null, code, ct);
            model.SelectedCopies.Add(new(copy.CopyCode, copy.BookTitle ?? "Không tìm thấy bản sao", copy.CopyStatus ?? "Không xác định"));
        }
        return View("Index", model);
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
