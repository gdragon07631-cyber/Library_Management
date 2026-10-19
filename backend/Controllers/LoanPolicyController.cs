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
            DebtLockThreshold = threshold,
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
        if (!ModelState.IsValid)
        {
            model.UpdatedAtUtc = policy?.UpdatedAtUtc;
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
        var previousThreshold = policy.DebtLockThreshold;
        var isUnchanged = policy.LoanDays == model.LoanDays
            && policy.DebtLockThreshold == model.DebtLockThreshold
            && dbContext.Entry(policy).State != EntityState.Added;
        if (isUnchanged)
        {
            TempData["SuccessMessage"] = "Chính sách mượn không thay đổi.";
            return RedirectToAction(nameof(Index));
        }

        policy.LoanDays = model.LoanDays;
        policy.DebtLockThreshold = model.DebtLockThreshold;
        policy.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        // Chỉ ghi phần thực sự thay đổi, giữ nguyên định dạng nhật ký số ngày mượn đã có.
        var changes = new List<string>();
        if (previousLoanDays != policy.LoanDays)
            changes.Add($"số ngày mượn {previousLoanDays} → {policy.LoanDays}");
        if (previousThreshold != policy.DebtLockThreshold)
            changes.Add($"ngưỡng nợ phí {previousThreshold:0.##} → {policy.DebtLockThreshold:0.##}");
        await auditLogService.WriteAsync(
            admin.Email,
            AuditActions.UpdateLoanPolicy,
            $"Chính sách mượn: {(changes.Count == 0 ? $"số ngày mượn {previousLoanDays} → {policy.LoanDays}" : string.Join("; ", changes))}",
            AuditLogService.ClientIp(HttpContext),
            cancellationToken);

        TempData["SuccessMessage"] = "Đã cập nhật chính sách mượn và ngưỡng đề xuất khoá thẻ.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("api/loan-policy/debt-threshold")]
    public async Task<IActionResult> GetDebtThresholdApi(CancellationToken cancellationToken = default)
    {
        // Dự án không đăng ký authentication scheme nên Forbid() sẽ ném lỗi; trả mã 403 trực tiếp.
        if (await GetSignedInAdminAsync(cancellationToken) is null)
            return ManagerOnly();

        var policy = await dbContext.LoanPolicies.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == LoanPolicy.SingletonId, cancellationToken);
        return Ok(new { debtLockThreshold = policy?.DebtLockThreshold ?? LoanPolicy.DefaultDebtLockThreshold });
    }

    [HttpPut("api/loan-policy/debt-threshold")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateDebtThresholdApi([FromBody] UpdateDebtThresholdRequest? request, CancellationToken cancellationToken = default)
    {
        var admin = await GetSignedInAdminAsync(cancellationToken);
        if (admin is null)
            return ManagerOnly();
        if (request?.DebtLockThreshold is not decimal threshold || threshold < 0m)
            return BadRequest(new { message = "Ngưỡng nợ phí phải là số không âm." });

        var policy = await dbContext.LoanPolicies
            .SingleOrDefaultAsync(item => item.Id == LoanPolicy.SingletonId, cancellationToken);
        if (policy is null)
        {
            policy = new LoanPolicy { Id = LoanPolicy.SingletonId };
            dbContext.LoanPolicies.Add(policy);
        }

        var previousThreshold = policy.DebtLockThreshold;
        policy.DebtLockThreshold = threshold;
        policy.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync(
            admin.Email,
            AuditActions.UpdateLoanPolicy,
            $"Chính sách mượn: ngưỡng nợ phí {previousThreshold:0.##} → {policy.DebtLockThreshold:0.##}",
            AuditLogService.ClientIp(HttpContext),
            cancellationToken);
        return Ok(new { debtLockThreshold = policy.DebtLockThreshold });
    }

    [HttpGet("api/loan-policy/lock-proposals")]
    public async Task<IActionResult> GetLockProposalsApi(CancellationToken cancellationToken = default)
    {
        if (await GetSignedInAdminAsync(cancellationToken) is null)
            return ManagerOnly();

        var policy = await dbContext.LoanPolicies.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == LoanPolicy.SingletonId, cancellationToken);
        var threshold = policy?.DebtLockThreshold ?? LoanPolicy.DefaultDebtLockThreshold;
        return Ok(await GetLockProposalsAsync(threshold, cancellationToken));
    }

    private ObjectResult ManagerOnly() =>
        StatusCode(StatusCodes.Status403Forbidden, new { message = "Chỉ Quản lý thư viện hoặc Quản trị hệ thống được thực hiện thao tác này." });

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
        // Cùng mốc "hôm nay" theo giờ thư viện như khi mượn/gia hạn (BookLoanService), tránh lệch ngày do UTC.
        var today = DateOnly.FromDateTime(DateTime.Today);
        // Phiếu chưa trả = chưa ghi nhận ReturnedAtUtc (cột mà chức năng nhận trả sách cập nhật).
        var overdueLoans = await dbContext.BookLoans.AsNoTracking()
            .Where(loan => loan.ReturnedAtUtc == null && loan.DueDate < today)
            .Select(loan => new
            {
                loan.Id,
                loan.ReaderAccountId,
                loan.DueDate,
                BookTitle = loan.Book != null ? loan.Book.Title : string.Empty,
                CopyCode = loan.BookCopy != null ? loan.BookCopy.CopyCode : null
            })
            .ToListAsync(cancellationToken);

        var overdueByReader = overdueLoans
            .GroupBy(loan => loan.ReaderAccountId)
            .ToDictionary(group => group.Key, group => group.Select(loan => new LockProposalLoanViewModel
            {
                LoanId = loan.Id,
                BookTitle = loan.BookTitle,
                CopyCode = loan.CopyCode,
                DueDate = loan.DueDate,
                OverdueDays = today.DayNumber - loan.DueDate.DayNumber
            }).ToList());

        var readers = await dbContext.ReaderAccounts.AsNoTracking()
            .Include(reader => reader.LibraryCard)
            .Where(reader => reader.LibraryCard != null)
            .OrderBy(reader => reader.FullName)
            .ToListAsync(cancellationToken);

        var proposals = new List<LockProposalViewModel>();
        foreach (var reader in readers)
        {
            var card = reader.LibraryCard;
            // Thẻ đang bị khoá không được đề xuất khoá lại; việc đề xuất không thay đổi trạng thái thẻ.
            if (card is null || LibraryCardStatusHelper.IsCardLocked(card, now))
                continue;

            var readerOverdueLoans = overdueByReader.TryGetValue(reader.Id, out var loans) ? loans : [];
            var maxOverdueDays = readerOverdueLoans.Count == 0 ? 0 : readerOverdueLoans.Max(loan => loan.OverdueDays);
            // Tái sử dụng cách tính nợ mà chức năng mượn/gia hạn đang dùng để chặn bạn đọc còn nợ phí.
            var debt = reader.TotalDebt;
            var isOverdueViolation = maxOverdueDays > LockProposalViewModel.OverdueDaysLimit;
            var isDebtViolation = debt > threshold;
            if (!isOverdueViolation && !isDebtViolation)
                continue;

            var reasons = new List<string>();
            if (isOverdueViolation)
                reasons.Add(LockProposalViewModel.OverdueReason);
            if (isDebtViolation)
                reasons.Add(LockProposalViewModel.DebtReason);

            proposals.Add(new LockProposalViewModel
            {
                ReaderAccountId = reader.Id,
                ReaderCode = reader.StudentOrStaffCode,
                ReaderName = reader.FullName,
                CardCode = card.CardCode,
                MaxOverdueDays = maxOverdueDays,
                OutstandingDebt = debt,
                DebtThreshold = threshold,
                Status = LibraryCardStatusHelper.GetEffectiveStatus(card, now),
                IsOverdueViolation = isOverdueViolation,
                IsDebtViolation = isDebtViolation,
                Reasons = reasons,
                OverdueLoans = readerOverdueLoans
                    .Where(loan => loan.OverdueDays > LockProposalViewModel.OverdueDaysLimit)
                    .OrderByDescending(loan => loan.OverdueDays)
                    .ToList()
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
