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
        var threshold = policy?.DebtLockThreshold ?? LoanPolicy.DefaultDebtLockThreshold;
        var model = new LoanPolicyViewModel
        {
            LoanDays = policy?.LoanDays ?? LoanPolicy.DefaultLoanDays,
<<<<<<< HEAD
            DailyOverdueFee = policy?.DailyOverdueFee,
=======
            DebtLockThreshold = threshold,
>>>>>>> origin/featture/S4-04/mo_khoa_the_ban_doc_va_ghi_nhat_ky_mo_khoa
            UpdatedAtUtc = policy?.UpdatedAtUtc,
            CardTypePolicies = await GetCardTypePoliciesAsync(cancellationToken),
            LockProposals = await GetLockProposalsAsync(threshold, cancellationToken)
        };
        return View(model);
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
            model.LockProposals = await GetLockProposalsAsync(policy?.DebtLockThreshold ?? model.DebtLockThreshold, cancellationToken);
            return View(model);
        }

        if (policy is null)
        {
            policy = new LoanPolicy { Id = LoanPolicy.SingletonId };
            dbContext.LoanPolicies.Add(policy);
        }

        var previousLoanDays = policy.LoanDays;
<<<<<<< HEAD
        var previousDailyOverdueFee = policy.DailyOverdueFee;
        if (policy.LoanDays == model.LoanDays && policy.DailyOverdueFee == model.DailyOverdueFee
            && dbContext.Entry(policy).State != EntityState.Added)
=======
        var previousThreshold = policy.DebtLockThreshold;
        var isUnchanged = policy.LoanDays == model.LoanDays
            && policy.DebtLockThreshold == model.DebtLockThreshold
            && dbContext.Entry(policy).State != EntityState.Added;
        if (isUnchanged)
>>>>>>> origin/featture/S4-04/mo_khoa_the_ban_doc_va_ghi_nhat_ky_mo_khoa
        {
            TempData["SuccessMessage"] = "Chính sách mượn không thay đổi.";
            return RedirectToAction(nameof(Index));
        }

        policy.LoanDays = model.LoanDays;
<<<<<<< HEAD
        policy.DailyOverdueFee = model.DailyOverdueFee;
=======
        policy.DebtLockThreshold = model.DebtLockThreshold;
>>>>>>> origin/featture/S4-04/mo_khoa_the_ban_doc_va_ghi_nhat_ky_mo_khoa
        policy.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync(
            admin.Email,
            AuditActions.UpdateLoanPolicy,
<<<<<<< HEAD
            $"Chính sách mượn: số ngày mượn {previousLoanDays} → {policy.LoanDays}; phí trễ hạn/ngày {previousDailyOverdueFee?.ToString("N2") ?? "chưa khai báo"} → {policy.DailyOverdueFee:N2} VNĐ",
=======
            $"Chính sách mượn: số ngày mượn {previousLoanDays} → {policy.LoanDays}; ngưỡng nợ phí {previousThreshold:0.##} → {policy.DebtLockThreshold:0.##}",
>>>>>>> origin/featture/S4-04/mo_khoa_the_ban_doc_va_ghi_nhat_ky_mo_khoa
            AuditLogService.ClientIp(HttpContext),
            cancellationToken);

        TempData["SuccessMessage"] = "Đã cập nhật chính sách mượn và ngưỡng đề xuất khoá thẻ.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("api/loan-policy/debt-threshold")]
    public async Task<IActionResult> GetDebtThresholdApi(CancellationToken cancellationToken = default)
    {
        if (await GetSignedInAdminAsync(cancellationToken) is null)
            return Forbid();

        var policy = await dbContext.LoanPolicies.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == LoanPolicy.SingletonId, cancellationToken);
        return Ok(new { debtLockThreshold = policy?.DebtLockThreshold ?? LoanPolicy.DefaultDebtLockThreshold });
    }

    [HttpGet("api/loan-policy/lock-proposals")]
    public async Task<IActionResult> GetLockProposalsApi(CancellationToken cancellationToken = default)
    {
        if (await GetSignedInAdminAsync(cancellationToken) is null)
            return Forbid();

        var policy = await dbContext.LoanPolicies.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == LoanPolicy.SingletonId, cancellationToken);
        var threshold = policy?.DebtLockThreshold ?? LoanPolicy.DefaultDebtLockThreshold;
        return Ok(await GetLockProposalsAsync(threshold, cancellationToken));
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

    private async Task<List<LockProposalViewModel>> GetLockProposalsAsync(decimal threshold, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var overdueByReader = await dbContext.BookLoans.AsNoTracking()
            .Where(loan => !loan.IsReturned && loan.DueDate < today)
            .Select(loan => new { loan.ReaderAccountId, loan.DueDate })
            .ToListAsync(cancellationToken);

        var maxOverdueDaysByReader = overdueByReader
            .GroupBy(item => item.ReaderAccountId)
            .ToDictionary(group => group.Key, group => group.Max(item => today.DayNumber - item.DueDate.DayNumber));

        var readers = await dbContext.ReaderAccounts.AsNoTracking()
            .Include(reader => reader.LibraryCard)
            .OrderBy(reader => reader.FullName)
            .ToListAsync(cancellationToken);

        var proposals = new List<LockProposalViewModel>();
        foreach (var reader in readers)
        {
            var card = reader.LibraryCard;
            if (card is null || LibraryCardStatusHelper.IsCardLocked(card, now))
                continue;

            var maxOverdueDays = maxOverdueDaysByReader.TryGetValue(reader.Id, out var value) ? value : 0;
            var reasons = new List<string>();
            if (maxOverdueDays > 30)
                reasons.Add("Quá hạn trả sách trên 30 ngày");
            if (reader.OutstandingBalance > threshold)
                reasons.Add("Nợ phí vượt ngưỡng");

            if (reasons.Count == 0)
                continue;

            proposals.Add(new LockProposalViewModel
            {
                ReaderAccountId = reader.Id,
                ReaderName = reader.FullName,
                CardCode = card.CardCode,
                MaxOverdueDays = maxOverdueDays,
                OutstandingDebt = reader.OutstandingBalance,
                DebtThreshold = threshold,
                Status = card.Status,
                Reasons = reasons
            });
        }

        return proposals
            .OrderByDescending(item => item.MaxOverdueDays)
            .ThenByDescending(item => item.OutstandingDebt)
            .ThenBy(item => item.ReaderName)
            .ToList();
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
