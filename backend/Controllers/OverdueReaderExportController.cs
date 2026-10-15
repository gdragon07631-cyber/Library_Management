using Microsoft.AspNetCore.Mvc;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

[StaffOnly(AccountRoles.LibraryManager)]
public sealed class OverdueReaderExportController(OverdueReaderExportService exports, IAuditLogService audit) : Controller
{
    [HttpGet("api/overdue-readers/card-types")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> CardTypes([FromServices] IReaderRegistrationService registration, CancellationToken ct) =>
        Ok((await registration.GetActiveCardTypesAsync(ct)).Select(type => new { id = type.Id, name = type.Name }));

    [HttpPost("api/overdue-readers/export"), ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Export(CancellationToken ct, [FromForm] string? minimumOverdueDays = null,
        [FromForm] string? libraryCardTypeId = null)
    {
        var staff = await audit.GetSignedInStaffAsync(Request, ct);
        if (staff is null) return Unauthorized(new { message = "Vui lòng đăng nhập bằng tài khoản nhân sự." });
        if (!ModelState.IsValid || !OverdueReaderExportFilter.TryParse(minimumOverdueDays, out var minimumDays))
            return BadRequest(new { message = OverdueReaderExportFilter.ValidationMessage });
        if (!OverdueReaderExportFilter.TryParseCardType(libraryCardTypeId, out var typeId))
            return BadRequest(new { message = OverdueReaderExportFilter.CardTypeValidationMessage });
        var result = await exports.ExportAsync(staff, AuditLogService.ClientIp(HttpContext), ct, minimumDays, typeId);
        if (result.Content is null) return StatusCode(result.StatusCode, new { message = result.Error });
        return File(result.Content, OverdueReaderExportService.ContentType, result.FileName);
    }
}
