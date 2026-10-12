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
        var signedInStaff = await audit.GetSignedInStaffAsync(Request, ct);
        model.CanOverride = signedInStaff != null && CanOverrideDirectLoan(signedInStaff.Role);
        if (loanId.HasValue) model.Receipt = await service.ReceiptAsync(loanId.Value, ct);
        if (!string.IsNullOrWhiteSpace(copyCode) && !loanId.HasValue)
            model.SelectedCopies = await service.ValidateRowsAsync([copyCode], ct: ct, cardCode: cardCode);
        foreach (var id in (loanIds ?? []).Distinct())
        {
            var receipt = await service.ReceiptAsync(id, ct);
            if (receipt != null) model.Receipts.Add(receipt);
        }
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> LookupCopy(string? cardCode, string? copyCode, CancellationToken ct, string? rowId = null)
    {
        var model = await service.LookupAsync(cardCode, copyCode, ct);
        if (model.ReaderName == null)
            return BadRequest(new { error = model.ReaderError ?? "Vui lòng nhập mã thẻ bạn đọc." });
        var rows = await service.ValidateRowsAsync([copyCode ?? ""], rowId == null ? null : [rowId], ct, cardCode);
        return Json(rows[0]);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmMany(DirectLoanBatchRequest request, CancellationToken ct)
    {
        var staff = await audit.GetSignedInStaffAsync(Request, ct);
        if (staff == null) return Unauthorized();
        if (request.RequestId == Guid.Empty) ModelState.AddModelError(nameof(request.RequestId), "Thiếu định danh lượt mượn. Vui lòng tải lại trang.");
        var result = ModelState.IsValid
            ? await service.ConfirmManyAsync(request.CardCode, request.CopyCodes, staff, ct, AuditLogService.ClientIp(HttpContext),
                request.RowIds.Count == 0 ? null : request.RowIds, request.RequestId, request.BypassReason, request.ConfirmBypass)
            : new DirectLoanBatchOutcome(false, "Thông tin mượn không hợp lệ.");
        if (result.IsSuccess)
        {
            TempData["DirectLoanSuccess"] = "Xác nhận mượn sách thành công.";
            if (Request.Headers.Accept.ToString().Contains("application/json"))
                return Json(new { isSuccess = true, url = Url.Action(nameof(Status), new { requestId = request.RequestId }) });
            return RedirectToAction(nameof(Index), new { cardCode = request.CardCode.Trim(), loanIds = result.Loans!.Select(l => l.Id).ToArray() });
        }
        ModelState.AddModelError(string.Empty, result.ErrorMessage ?? "Không thể tạo phiếu mượn.");
        if (Request.Headers.Accept.ToString().Contains("application/json"))
            return Json(new { result.IsSuccess, result.IsUncertain, result.RequestConflict, result.ErrorMessage, result.Rows });
        if (result.IsUncertain || result.RequestConflict)
            return View("Index", new DirectLoanViewModel { CardCode = request.CardCode, RequestId = request.RequestId, VerifyOnly = true,
                CanOverride = CanOverrideDirectLoan(staff.Role),
                SelectedCopies = request.CopyCodes.Select((c, i) => new DirectLoanSelection(c, "", "")
                { RowId = i < request.RowIds.Count ? request.RowIds[i] : Guid.NewGuid().ToString("N"), Error = "Đang xác minh kết quả" }).ToList() });
        var model = await service.LookupAsync(request.CardCode, null, ct);
        var currentStaff = await audit.GetSignedInStaffAsync(Request, ct);
        model.CanOverride = currentStaff != null && CanOverrideDirectLoan(currentStaff.Role);
        model.RequestId = request.RequestId;
        model.Retrying = true;
        model.SelectedCopies = result.Rows?.ToList() ?? await service.ValidateRowsAsync(request.CopyCodes,
            request.RowIds.Count == 0 ? null : request.RowIds, ct, request.CardCode);
        return View("Index", model);
    }

    [HttpGet]
    public async Task<IActionResult> Status(Guid requestId, CancellationToken ct)
    {
        var staff = await audit.GetSignedInStaffAsync(Request, ct);
        if (staff == null) return Unauthorized();
        var result = await service.VerifyAsync(requestId, staff.Id);
        if (Request.Headers.Accept.ToString().Contains("application/json"))
            return Json(new { result.IsSuccess, result.IsUncertain, result.RequestConflict, result.ErrorMessage,
                url = result.IsSuccess ? Url.Action(nameof(Status), new { requestId }) : null });
        if (result.RequestConflict) return Forbid();
        var model = new DirectLoanViewModel { RequestId = requestId, VerifyOnly = result.IsUncertain, Retrying = true };
        var currentStaff = await audit.GetSignedInStaffAsync(Request, ct);
        model.CanOverride = currentStaff != null && CanOverrideDirectLoan(currentStaff.Role);
        if (result.Operation is { } operation)
        {
            model = await service.LookupAsync(operation.CardCode, null, ct);
            model.CanOverride = currentStaff != null && CanOverrideDirectLoan(currentStaff.Role);
            model.RequestId = requestId;
            model.Retrying = true;
            if (result.IsSuccess)
            {
                model.Receipts = result.Loans!.ToList();
                TempData["DirectLoanSuccess"] = "Xác nhận mượn sách thành công.";
            }
            else model.SelectedCopies = await service.ValidateRowsAsync(
                System.Text.Json.JsonSerializer.Deserialize<string[]>(operation.CopyCodesJson)!,
                System.Text.Json.JsonSerializer.Deserialize<string[]>(operation.RowIdsJson)!, ct, operation.CardCode);
        }
        if (!result.IsSuccess) ModelState.AddModelError("", result.ErrorMessage!);
        return View("Index", model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(DirectLoanRequest request, CancellationToken ct)
    {
        return await ConfirmMany(new DirectLoanBatchRequest { RequestId = request.RequestId, CardCode = request.CardCode,
            CopyCodes = [request.CopyCode], BypassReason = request.BypassReason, ConfirmBypass = request.ConfirmBypass }, ct);
    }

    private static bool CanOverrideDirectLoan(string role) =>
        role is AccountRoles.Librarian or AccountRoles.LibraryManager or AccountRoles.SystemAdmin;
}
