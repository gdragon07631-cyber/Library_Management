using Microsoft.AspNetCore.Mvc;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

/// <summary>System-administrator-only, read-only review of actual versus approved permissions.</summary>
[StaffOnly(AccountRoles.SystemAdmin)]
public sealed class PermissionMatrixController(IPermissionMatrixService permissionMatrixService) : Controller
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Index() => View(permissionMatrixService.GetReview());

    [HttpGet("api/admin/permission-matrix")]
    [Produces("application/json")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Get() => Ok(permissionMatrixService.GetReview());
}
