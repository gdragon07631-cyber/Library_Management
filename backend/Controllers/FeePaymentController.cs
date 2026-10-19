using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

[StaffOnly(AccountRoles.Librarian)]
public sealed class FeePaymentController(IFeePaymentService payments, IAuditLogService auditLogService, ApplicationDbContext? db = null) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(string? search, int? readerId, CancellationToken cancellationToken = default)
    {
        var model = await PageAsync(search, readerId, cancellationToken);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Collect(CollectFeeViewModel payment, CancellationToken cancellationToken = default)
    {
        var staff = await auditLogService.GetSignedInStaffAsync(Request, cancellationToken);
        if (staff is null) return Unauthorized();
        if (staff.Role != AccountRoles.Librarian) return Forbid();
        if (ModelState.IsValid)
        {
            var result = await payments.CollectAsync(payment, staff.Id, cancellationToken);
            if (result.IsSuccess)
            {
                TempData["SuccessMessage"] = result.Message;
                return RedirectToAction(nameof(Index), new { readerId = payment.ReaderAccountId });
            }
            ModelState.AddModelError(string.Empty, result.Message);
        }
        else ModelState.AddModelError(string.Empty, "Số tiền hoặc thông tin thanh toán không hợp lệ.");
        var model = await PageAsync(null, payment.ReaderAccountId, cancellationToken);
        model.Payment = payment;
        return View("Index", model);
    }

    private async Task<FeePaymentIndexViewModel> PageAsync(string? search, int? readerId, CancellationToken cancellationToken)
    {
        var debt = readerId.HasValue ? await payments.GetDebtAsync(readerId.Value, cancellationToken) : null;
        if (readerId.HasValue && debt is null) ModelState.AddModelError(string.Empty, "Không tìm thấy bạn đọc.");
        var history = debt is null || db is null ? [] : await db.FeePayments.AsNoTracking()
            .Where(p => p.ReaderAccountId == debt.Reader.Id)
            .Include(p => p.CollectedByAdminAccount).Include(p => p.Allocations)
            .OrderByDescending(p => p.PaidAtUtc).ThenByDescending(p => p.Id).ToListAsync(cancellationToken);
        return new()
        {
            Search = search, Readers = await payments.SearchAsync(search, cancellationToken), Debt = debt,
            Payment = new() { ReaderAccountId = readerId ?? 0, RequestId = Guid.NewGuid() },
            History = history.Select(p => new FeePaymentHistoryItem(p.Id, p.Amount, p.BalanceAfter, p.PaidAtUtc,
                p.CollectedByAdminAccount is { } staff ? (string.IsNullOrWhiteSpace(staff.FullName) ? staff.Email : staff.FullName) : $"#{p.CollectedByAdminAccountId}",
                p.Allocations.OrderBy(a => a.ReaderFeeId).Select(a => new FeePaymentHistoryAllocation(a.ReaderFeeId, a.Amount)).ToList())).ToList()
        };
    }
}
