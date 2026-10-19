using Microsoft.AspNetCore.Mvc;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

/// <summary>System-administrator management and review of role permissions.</summary>
[StaffOnly(AccountRoles.SystemAdmin)]
public sealed class PermissionMatrixController(
    IPermissionMatrixService permissionMatrixService,
    IAuditLogService? auditLogService = null) : Controller
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Index() => View(permissionMatrixService.GetReview());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save([FromForm] List<string>? permissions, CancellationToken cancellationToken = default)
    {
        permissionMatrixService.UpdatePermissions(permissions ?? []);
        if (auditLogService != null)
        {
            var staff = await auditLogService.GetSignedInStaffAsync(Request, cancellationToken);
            if (staff != null)
            {
                await auditLogService.WriteAsync(
                    staff.Email,
                    "UpdatePermissionMatrix",
                    $"Cập nhật ma trận phân quyền hệ thống ({permissions?.Count ?? 0} quyền được bật)",
                    AuditLogService.ClientIp(HttpContext),
                    cancellationToken);
            }
        }

        TempData["SuccessMessage"] = "Đã lưu cấu hình Ma trận phân quyền hệ thống thành công.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reset(CancellationToken cancellationToken = default)
    {
        permissionMatrixService.ResetToDefault();
        if (auditLogService != null)
        {
            var staff = await auditLogService.GetSignedInStaffAsync(Request, cancellationToken);
            if (staff != null)
            {
                await auditLogService.WriteAsync(
                    staff.Email,
                    "ResetPermissionMatrix",
                    "Khôi phục ma trận phân quyền hệ thống về chuẩn mặc định",
                    AuditLogService.ClientIp(HttpContext),
                    cancellationToken);
            }
        }

        TempData["SuccessMessage"] = "Đã khôi phục Ma trận phân quyền về chuẩn mặc định.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("api/admin/permission-matrix")]
    [Produces("application/json")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Get() => Ok(permissionMatrixService.GetReview());
}
