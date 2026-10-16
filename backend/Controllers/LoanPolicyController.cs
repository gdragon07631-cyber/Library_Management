using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

/// <summary>Quản trị hệ thống xem và sửa chính sách mượn.</summary>
public sealed class LoanPolicyController(
    ApplicationDbContext dbContext,
    IAuditLogService auditLogService) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken = default)
    {
        if (await GetSignedInAdminAsync(cancellationToken) is null)
            return RedirectToAction("Login", "Account", new { returnUrl = Url.Action(nameof(Index)) });

        var policy = await dbContext.LoanPolicies.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == LoanPolicy.SingletonId, cancellationToken);
        return View(new LoanPolicyViewModel
        {
            LoanDays = policy?.LoanDays ?? LoanPolicy.DefaultLoanDays,
            DailyOverdueFee = policy?.DailyOverdueFee,
            UpdatedAtUtc = policy?.UpdatedAtUtc,
            CardTypePolicies = await GetCardTypePoliciesAsync(cancellationToken)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(LoanPolicyViewModel model, CancellationToken cancellationToken = default)
    {
        var admin = await GetSignedInAdminAsync(cancellationToken);
        if (admin is null)
            return RedirectToAction("Login", "Account", new { returnUrl = Url.Action(nameof(Index)) });

        var policy = await dbContext.LoanPolicies
            .SingleOrDefaultAsync(item => item.Id == LoanPolicy.SingletonId, cancellationToken);
        if (model.DailyOverdueFee is null && ModelState.IsValid)
            ModelState.AddModelError(nameof(model.DailyOverdueFee), "Vui lòng nhập mức phí trễ hạn mỗi ngày.");
        if (!ModelState.IsValid)
        {
            model.UpdatedAtUtc = policy?.UpdatedAtUtc;
            model.DailyOverdueFee = policy?.DailyOverdueFee;
            model.CardTypePolicies = await GetCardTypePoliciesAsync(cancellationToken);
            return View(model);
        }

        if (policy is null)
        {
            policy = new LoanPolicy { Id = LoanPolicy.SingletonId };
            dbContext.LoanPolicies.Add(policy);
        }

        var previousLoanDays = policy.LoanDays;
        var previousDailyOverdueFee = policy.DailyOverdueFee;
        if (policy.LoanDays == model.LoanDays && policy.DailyOverdueFee == model.DailyOverdueFee
            && dbContext.Entry(policy).State != EntityState.Added)
        {
            TempData["SuccessMessage"] = "Chính sách mượn không thay đổi.";
            return RedirectToAction(nameof(Index));
        }

        policy.LoanDays = model.LoanDays;
        policy.DailyOverdueFee = model.DailyOverdueFee;
        policy.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync(
            admin.Email,
            AuditActions.UpdateLoanPolicy,
            $"Chính sách mượn: số ngày mượn {previousLoanDays} → {policy.LoanDays}; phí trễ hạn/ngày {previousDailyOverdueFee?.ToString("N2") ?? "chưa khai báo"} → {policy.DailyOverdueFee:N2} VNĐ",
            AuditLogService.ClientIp(HttpContext),
            cancellationToken);

        TempData["SuccessMessage"] = "Đã cập nhật chính sách mượn.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateCardTypeLoanDays(UpdateCardTypeLoanDaysViewModel model, CancellationToken cancellationToken = default)
    {
        var admin = await GetSignedInAdminAsync(cancellationToken);
        if (admin is null)
            return RedirectToAction("Login", "Account", new { returnUrl = Url.Action(nameof(Index)) });

        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = ModelState.Values.SelectMany(value => value.Errors).FirstOrDefault()?.ErrorMessage
                ?? "Số ngày mượn cho loại thẻ không hợp lệ.";
            return RedirectToAction(nameof(Index));
        }

        var cardType = await dbContext.LibraryCardTypes
            .SingleOrDefaultAsync(type => type.Id == model.LibraryCardTypeId, cancellationToken);
        if (cardType is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy loại thẻ cần cập nhật.";
            return RedirectToAction(nameof(Index));
        }

        var previousDays = cardType.LoanDays;
        cardType.LoanDays = model.LoanDays!.Value;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync(
            admin.Email,
            AuditActions.UpdateLoanPolicy,
            $"Chính sách mượn loại thẻ {cardType.Name}: số ngày {(previousDays?.ToString() ?? "chưa cấu hình")} → {cardType.LoanDays}",
            AuditLogService.ClientIp(HttpContext),
            cancellationToken);

        TempData["SuccessMessage"] = $"Đã cập nhật số ngày mượn cho loại thẻ {cardType.Name}.";
        return RedirectToAction(nameof(Index));
    }

    private Task<List<CardTypeLoanDaysViewModel>> GetCardTypePoliciesAsync(CancellationToken cancellationToken) =>
        dbContext.LibraryCardTypes.AsNoTracking().OrderBy(type => type.Name)
            .Select(type => new CardTypeLoanDaysViewModel
            {
                LibraryCardTypeId = type.Id,
                Name = type.Name,
                LoanDays = type.LoanDays
            })
            .ToListAsync(cancellationToken);

    private async Task<AdminAccount?> GetSignedInAdminAsync(CancellationToken cancellationToken)
    {
        var staff = await auditLogService.GetSignedInStaffAsync(Request, cancellationToken);
        return staff?.Role is AccountRoles.SystemAdmin or AccountRoles.LibraryManager ? staff : null;
    }
}
