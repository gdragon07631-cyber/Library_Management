using Microsoft.AspNetCore.Mvc;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

[StaffOnly(AccountRoles.Librarian, AccountRoles.LibraryManager, AccountRoles.SystemAdmin)]
public sealed class BookIncidentController(
    IBookIncidentService incidentService,
    IAuditLogService auditLogService) : Controller
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

        var staffEmail = await GetSignedInStaffEmailAsync(ct);
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

        TempData["SuccessMessage"] =
            $"Ghi nhận sự cố sách {model.IncidentType} thành công. Bản sao đã được chuyển sang trạng thái Đã loại khỏi kho.";
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

        var staffEmail = await GetSignedInStaffEmailAsync(ct);
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
            message = $"Ghi nhận sự cố sách {model.IncidentType} thành công. Bản sao đã được chuyển sang trạng thái Đã loại khỏi kho.",
            incidentId = outcome.Incident!.Id,
            copyStatus = outcome.UpdatedCopy!.Status,
            copyCondition = outcome.UpdatedCopy!.PhysicalCondition
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

        return new BookIncidentIndexViewModel
        {
            OpenLoans = openLoans,
            SelectedLoanId = selectedLoanId,
            SelectedLoanCopies = selectedLoanCopies,
            RecentIncidents = recentIncidents,
            FormModel = new RecordBookIncidentViewModel
            {
                LoanId = selectedLoanId ?? 0,
                BookCopyId = selectedLoanCopies.Count == 1 ? selectedLoanCopies[0].CopyId : 0
            }
        };
    }

    private async Task<string> GetSignedInStaffEmailAsync(CancellationToken ct)
    {
        try
        {
            if (Request?.Cookies != null)
            {
                var staff = await auditLogService.GetSignedInStaffAsync(Request, ct);
                if (!string.IsNullOrWhiteSpace(staff?.Email)) return staff.Email;
            }
        }
        catch { /* ignore */ }
        return "Thủ thư";
    }
}
