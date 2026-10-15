using Microsoft.AspNetCore.Mvc;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

[StaffOnly(AccountRoles.Librarian, AccountRoles.LibraryManager, AccountRoles.SystemAdmin)]
public sealed class BookIncidentController(
    IBookIncidentService incidentService,
    IAuditLogService auditLogService,
    ICompensationService? compensationService = null) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index([FromQuery] long? loanId = null, CancellationToken ct = default)
    {
        var model = await BuildIndexModelAsync(loanId, ct);
        return View(model);
    }

    [HttpGet("api/book-incidents/open-loans")]
    public async Task<IActionResult> GetOpenLoans(CancellationToken ct = default)
    {
        var loans = await incidentService.GetOpenLoansAsync(ct);
        return Ok(loans);
    }

    [HttpGet("api/book-incidents/loans/{loanId:long}/copies")]
    public async Task<IActionResult> GetCopiesForLoan(long loanId, CancellationToken ct = default)
    {
        var copies = await incidentService.GetCopiesForLoanAsync(loanId, ct);
        return Ok(copies);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Record(
        RecordBookIncidentViewModel model,
        IFormFile? photo = null,
        CancellationToken ct = default)
    {
        var effectivePhoto = photo ?? model.Photo;

        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage)
                .FirstOrDefault(msg => !string.IsNullOrWhiteSpace(msg))
                ?? "Dữ liệu ghi nhận sự cố không hợp lệ.";

            var indexModel = await BuildIndexModelAsync(model.LoanId, ct);
            indexModel.FormModel = model;
            return View(nameof(Index), indexModel);
        }

        var (_, staffEmail) = await GetSignedInStaffInfoAsync(ct);
        var ipAddress = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "127.0.0.1";

        var request = new RecordIncidentRequest
        {
            LoanId = model.LoanId,
            BookCopyId = model.BookCopyId,
            IncidentType = model.IncidentType,
            Description = model.Description,
            Photo = effectivePhoto,
            StaffEmail = staffEmail,
            IpAddress = ipAddress
        };

        var outcome = await incidentService.RecordIncidentAsync(request, ct);
        if (!outcome.IsSuccess)
        {
            TempData["ErrorMessage"] = outcome.ErrorMessage;
            var indexModel = await BuildIndexModelAsync(model.LoanId, ct);
            indexModel.FormModel = model;
            return View(nameof(Index), indexModel);
        }

        var successMsg = $"Ghi nhận sự cố sách {model.IncidentType} thành công. Đã đóng phiếu mượn #{model.LoanId} (Lý do: {outcome.CloseReason ?? model.IncidentType}). Bản sao đã được chuyển sang trạng thái Đã loại khỏi kho.";
        if (outcome.OverdueFee > 0)
        {
            successMsg += $" Phí trễ hạn: {outcome.OverdueFee:N0}đ.";
        }
        else
        {
            successMsg += " Phí trễ hạn: 0đ.";
        }

        if (outcome.Compensation != null)
        {
            successMsg += $" Tiền bồi thường ban đầu: {outcome.Compensation.InitialAmount:N0}đ (Hệ số: {outcome.Compensation.AppliedRate}).";
        }
        else if (!string.IsNullOrWhiteSpace(outcome.CompensationMessage))
        {
            successMsg += $" ({outcome.CompensationMessage})";
        }

        TempData["SuccessMessage"] = successMsg;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("api/book-incidents/record")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RecordApi(
        [FromForm] RecordBookIncidentViewModel model,
        IFormFile? photo = null,
        CancellationToken ct = default)
    {
        var effectivePhoto = photo ?? model.Photo;

        if (!ModelState.IsValid)
        {
            var error = ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage)
                .FirstOrDefault(msg => !string.IsNullOrWhiteSpace(msg))
                ?? "Dữ liệu ghi nhận sự cố không hợp lệ.";
            return BadRequest(new { success = false, message = error });
        }

        var (_, staffEmail) = await GetSignedInStaffInfoAsync(ct);
        var ipAddress = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "127.0.0.1";

        var request = new RecordIncidentRequest
        {
            LoanId = model.LoanId,
            BookCopyId = model.BookCopyId,
            IncidentType = model.IncidentType,
            Description = model.Description,
            Photo = effectivePhoto,
            StaffEmail = staffEmail,
            IpAddress = ipAddress
        };

        var outcome = await incidentService.RecordIncidentAsync(request, ct);
        if (!outcome.IsSuccess)
        {
            return BadRequest(new { success = false, message = outcome.ErrorMessage });
        }

        return Ok(new
        {
            success = true,
            message = $"Ghi nhận sự cố sách {model.IncidentType} thành công. Đã đóng phiếu mượn #{model.LoanId} (Lý do: {outcome.CloseReason ?? model.IncidentType}). Bản sao đã được chuyển sang trạng thái Đã loại khỏi kho.",
            incidentId = outcome.Incident!.Id,
            loanId = outcome.ClosedLoan?.Id ?? model.LoanId,
            closeReason = outcome.CloseReason,
            overdueFee = outcome.OverdueFee,
            copyStatus = outcome.UpdatedCopy!.Status,
            copyCondition = outcome.UpdatedCopy!.PhysicalCondition,
            compensation = outcome.Compensation != null ? new
            {
                initialAmount = outcome.Compensation.InitialAmount,
                currentAmount = outcome.Compensation.CurrentAmount,
                appliedRate = outcome.Compensation.AppliedRate,
                recordedCoverPrice = outcome.Compensation.RecordedCoverPrice
            } : null,
            compensationMessage = outcome.CompensationMessage
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdjustCompensation(AdjustCompensationViewModel model, CancellationToken ct = default)
    {
        if (compensationService == null)
        {
            TempData["ErrorMessage"] = "Dịch vụ bồi thường chưa khả dụng.";
            return RedirectToAction(nameof(Index));
        }

        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage)
                .FirstOrDefault(msg => !string.IsNullOrWhiteSpace(msg))
                ?? "Dữ liệu điều chỉnh bồi thường không hợp lệ.";
            return RedirectToAction(nameof(Index));
        }

        var (role, email) = await GetSignedInStaffInfoAsync(ct);
        var outcome = await compensationService.AdjustCompensationAsync(
            new AdjustCompensationRequest
            {
                IncidentId = model.IncidentId,
                NewAmount = model.NewAmount,
                Reason = model.Reason
            },
            role,
            email,
            ct);

        if (!outcome.IsSuccess)
        {
            TempData["ErrorMessage"] = outcome.ErrorMessage;
        }
        else
        {
            TempData["SuccessMessage"] =
                $"Điều chỉnh tiền bồi thường sự cố #{model.IncidentId} thành {model.NewAmount:N0}đ thành công.";
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("api/book-incidents/{incidentId:long}/compensation/adjust")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdjustCompensationApi(
        long incidentId,
        [FromBody] AdjustCompensationRequest request,
        CancellationToken ct = default)
    {
        if (compensationService == null)
            return StatusCode(StatusCodes.Status500InternalServerError, new { success = false, message = "Dịch vụ bồi thường chưa khả dụng." });

        request.IncidentId = incidentId;
        var (role, email) = await GetSignedInStaffInfoAsync(ct);
        var outcome = await compensationService.AdjustCompensationAsync(request, role, email, ct);

        if (!outcome.IsSuccess)
        {
            return BadRequest(new { success = false, message = outcome.ErrorMessage });
        }

        return Ok(new
        {
            success = true,
            message = $"Điều chỉnh số tiền bồi thường thành {outcome.Compensation!.CurrentAmount:N0}đ thành công.",
            currentAmount = outcome.Compensation.CurrentAmount,
            initialAmount = outcome.Compensation.InitialAmount,
            isAdjusted = outcome.Compensation.IsAdjusted,
            adjustmentReason = outcome.Compensation.AdjustmentReason
        });
    }

    private async Task<BookIncidentIndexViewModel> BuildIndexModelAsync(long? selectedLoanId, CancellationToken ct)
    {
        var openLoans = await incidentService.GetOpenLoansAsync(ct);
        IReadOnlyList<BookCopyIncidentItemViewModel> selectedLoanCopies = [];

        if (selectedLoanId.HasValue && selectedLoanId.Value > 0)
        {
            selectedLoanCopies = await incidentService.GetCopiesForLoanAsync(selectedLoanId.Value, ct);
        }

        var recentIncidents = await incidentService.GetRecentIncidentsAsync(20, ct);
        var policy = compensationService != null ? await compensationService.GetPolicyAsync(ct) : null;

        return new BookIncidentIndexViewModel
        {
            OpenLoans = openLoans,
            SelectedLoanId = selectedLoanId,
            SelectedLoanCopies = selectedLoanCopies,
            RecentIncidents = recentIncidents,
            Policy = policy,
            FormModel = new RecordBookIncidentViewModel
            {
                LoanId = selectedLoanId ?? 0,
                BookCopyId = selectedLoanCopies.Count == 1 ? selectedLoanCopies[0].CopyId : 0
            }
        };
    }

    private async Task<(string Role, string Email)> GetSignedInStaffInfoAsync(CancellationToken ct)
    {
        try
        {
            if (Request?.Cookies != null)
            {
                var staff = await auditLogService.GetSignedInStaffAsync(Request, ct);
                if (staff != null) return (staff.Role, staff.Email);
            }
        }
        catch { /* ignore */ }

        if (User?.Identity?.IsAuthenticated == true)
        {
            var role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? AccountRoles.Librarian;
            var email = User.Identity.Name ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "Thủ thư";
            return (role, email);
        }

        return (AccountRoles.Librarian, "Thủ thư");
    }
}
