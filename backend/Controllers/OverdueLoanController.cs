using Microsoft.AspNetCore.Mvc;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

/// <summary>Danh sách chỉ đọc để thủ thư ưu tiên nhắc các phiếu mượn trễ lâu nhất.</summary>
[StaffOnly(AccountRoles.Librarian, AccountRoles.SystemAdmin, AccountRoles.LibraryManager)]
public sealed class OverdueLoanController(
    IBookLoanService loans,
    ILoanContactHistoryService contactHistories,
    IAuditLogService auditLogService) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(string? overdueRange, CancellationToken ct = default)
    {
        var range = OverdueLoanRanges.Parse(overdueRange);
        return View(new OverdueLoanViewModel
        {
            Range = range,
            Items = await loans.GetOverdueAsync(HoldPickupExpirySchedule.GetBusinessDate(DateTime.UtcNow), range, ct)
        });
    }

    [HttpGet("api/overdue-loans")]
    public async Task<IActionResult> GetOverdueApi(string? overdueRange, CancellationToken ct = default)
    {
        var range = OverdueLoanRanges.Parse(overdueRange);
        var items = await loans.GetOverdueAsync(HoldPickupExpirySchedule.GetBusinessDate(DateTime.UtcNow), range, ct);
        return Ok(items.Select(item => new
        {
            loanId = item.LoanId,
            daysOverdue = item.DaysOverdue,
            readerName = item.ReaderName,
            phoneNumber = item.PhoneNumber,
            cardCode = item.CardCode,
            bookTitle = item.BookTitle,
            loanDate = item.LoanDate,
            dueDate = item.DueDate,
            copyBarcode = item.CopyBarcode,
            latestContact = item.LatestContact is null ? null : new
            {
                item.LatestContact.Id,
                item.LatestContact.CreatedAtUtc,
                item.LatestContact.Note,
                item.LatestContact.ContactedBy
            }
        }));
    }

    [HttpGet]
    public async Task<IActionResult> History(long id, CancellationToken ct = default) =>
        View(new LoanContactHistoryPageViewModel
        {
            LoanId = id,
            Items = await contactHistories.GetByLoanIdAsync(id, ct)
        });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddContact(CreateLoanContactHistoryViewModel model, string? overdueRange, CancellationToken ct = default)
    {
        var staff = await auditLogService.GetSignedInStaffAsync(Request, ct);
        if (staff is null) return Unauthorized();

        var result = await contactHistories.AddAsync(model.LoanId, model.Note, staff, ct);
        TempData[result.IsSuccess ? "SuccessMessage" : "ErrorMessage"] = result.IsSuccess
            ? "Đã ghi nhận liên hệ với bạn đọc."
            : result.ErrorMessage;
        return RedirectToAction(nameof(Index), new { overdueRange });
    }

    [HttpGet("api/overdue-loans/{loanId:long}/contact-histories")]
    public async Task<IActionResult> GetContactHistoriesApi(long loanId, CancellationToken ct = default) =>
        Ok(await contactHistories.GetByLoanIdAsync(loanId, ct));

    [HttpPost("api/overdue-loans/{loanId:long}/contact-histories")]
    public async Task<IActionResult> AddContactHistoryApi(long loanId, [FromBody] CreateLoanContactHistoryViewModel? model, CancellationToken ct = default)
    {
        var staff = await auditLogService.GetSignedInStaffAsync(Request, ct);
        if (staff is null) return Unauthorized();

        var result = await contactHistories.AddAsync(loanId, model?.Note, staff, ct);
        return result.IsSuccess ? Created($"/api/overdue-loans/{loanId}/contact-histories/{result.Item!.Id}", result.Item)
            : BadRequest(new { message = result.ErrorMessage });
    }
}
