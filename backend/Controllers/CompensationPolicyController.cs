using Microsoft.AspNetCore.Mvc;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

[StaffOnly(AccountRoles.LibraryManager, AccountRoles.SystemAdmin)]
public sealed class CompensationPolicyController(
    ICompensationService compensationService,
    IAuditLogService auditLogService) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var policy = await compensationService.GetPolicyAsync(ct);
        var model = new CompensationPolicyViewModel
        {
            DefaultRate = policy?.DefaultRate ?? CompensationPolicy.DefaultMultiplier,
            LostRate = policy?.LostRate,
            DamagedRate = policy?.DamagedRate,
            UpdatedAtUtc = policy?.UpdatedAtUtc,
            UpdatedBy = policy?.UpdatedBy
        };
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(CompensationPolicyViewModel model, CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
        {
            var policy = await compensationService.GetPolicyAsync(ct);
            model.UpdatedAtUtc = policy?.UpdatedAtUtc;
            model.UpdatedBy = policy?.UpdatedBy;
            return View(nameof(Index), model);
        }

        var (staffRole, staffEmail) = await GetSignedInStaffInfoAsync(ct);

        var outcome = await compensationService.UpdatePolicyAsync(
            new UpdateCompensationPolicyRequest
            {
                DefaultRate = model.DefaultRate,
                LostRate = model.LostRate,
                DamagedRate = model.DamagedRate
            },
            staffRole,
            staffEmail,
            ct);

        if (!outcome.IsSuccess)
        {
            TempData["ErrorMessage"] = outcome.ErrorMessage;
            var policy = await compensationService.GetPolicyAsync(ct);
            model.UpdatedAtUtc = policy?.UpdatedAtUtc;
            model.UpdatedBy = policy?.UpdatedBy;
            return View(nameof(Index), model);
        }

        TempData["SuccessMessage"] = "Đã cập nhật hệ số bồi thường thành công.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("api/compensation-policy")]
    public async Task<IActionResult> GetPolicyApi(CancellationToken ct = default)
    {
        var policy = await compensationService.GetPolicyAsync(ct);
        return Ok(new
        {
            defaultRate = policy?.DefaultRate ?? CompensationPolicy.DefaultMultiplier,
            lostRate = policy?.LostRate,
            damagedRate = policy?.DamagedRate,
            updatedAtUtc = policy?.UpdatedAtUtc,
            updatedBy = policy?.UpdatedBy
        });
    }

    [HttpPost("api/compensation-policy")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdatePolicyApi([FromBody] UpdateCompensationPolicyRequest request, CancellationToken ct = default)
    {
        var (staffRole, staffEmail) = await GetSignedInStaffInfoAsync(ct);

        var outcome = await compensationService.UpdatePolicyAsync(request, staffRole, staffEmail, ct);
        if (!outcome.IsSuccess)
        {
            return BadRequest(new { success = false, message = outcome.ErrorMessage });
        }

        return Ok(new
        {
            success = true,
            message = "Đã cập nhật hệ số bồi thường thành công.",
            defaultRate = outcome.Policy!.DefaultRate,
            lostRate = outcome.Policy.LostRate,
            damagedRate = outcome.Policy.DamagedRate
        });
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
            var role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? string.Empty;
            var email = User.Identity.Name ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "Quản lý";
            return (role, email);
        }

        return (string.Empty, "Chưa xác thực");
    }
}
