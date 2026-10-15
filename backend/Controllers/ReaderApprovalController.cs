using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

/// <summary>Trang nghiệp vụ dành cho thủ thư duyệt hồ sơ bạn đọc.</summary>
[StaffOnly(AccountRoles.Librarian)]
public sealed class ReaderApprovalController(
    IReaderRegistrationService registrationService,
    ApplicationDbContext dbContext,
    IAuditLogService auditLogService) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken = default)
    {
        if (!await IsLibrarianSignedInAsync(cancellationToken))
            return RedirectToAction("Login", "Librarian", new { returnUrl = Url.Action(nameof(Index)) });

        return View(new ReaderApprovalIndexViewModel
        {
            PendingReaders = await registrationService.GetPendingReadersAsync(cancellationToken),
            CardTypes = await registrationService.GetActiveCardTypesAsync(cancellationToken)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(ApproveReaderViewModel model, CancellationToken cancellationToken = default)
    {
        if (!await IsLibrarianSignedInAsync(cancellationToken))
            return Unauthorized();

        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = "Thông tin cấp thẻ không hợp lệ.";
            return RedirectToAction(nameof(Index));
        }

        var outcome = await registrationService.ApproveReaderAsync(model, cancellationToken);
        if (!outcome.IsSuccess)
        {
            TempData["ErrorMessage"] = outcome.ErrorMessage;
            return RedirectToAction(nameof(Index));
        }

        await WriteIssueCardLogAsync(outcome.LibraryCard!, cancellationToken);
        TempData["SuccessMessage"] = $"Đã duyệt hồ sơ và cấp thẻ {outcome.LibraryCard!.CardCode}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("api/reader-registrations/pending")]
    public async Task<IActionResult> GetPendingReadersApi(
        [FromQuery] string? search,
        [FromQuery] DateOnly? fromDate,
        [FromQuery] DateOnly? toDate,
        CancellationToken cancellationToken = default)
    {
        if (!await IsLibrarianSignedInAsync(cancellationToken))
            return Unauthorized(new { message = "Thủ thư cần đăng nhập trước khi xem hồ sơ." });
        if (fromDate.HasValue && toDate.HasValue && fromDate.Value > toDate.Value)
            return BadRequest(new { message = "Từ ngày không được lớn hơn đến ngày." });

        var readers = await registrationService.GetPendingReadersAsync(search, fromDate, toDate, cancellationToken);
        return Ok(readers.Select(reader => new
        {
            id = reader.Id,
            fullName = reader.FullName,
            email = reader.Email,
            phoneNumber = reader.PhoneNumber,
            studentOrStaffCode = reader.StudentOrStaffCode,
            status = reader.Status,
            createdAtUtc = reader.CreatedAtUtc,
            emailConfirmed = reader.EmailConfirmed
        }));
    }

    [HttpPost("api/reader-registrations/{id}/approve")]
    public async Task<IActionResult> ApproveApi(
        int id,
        [FromBody] ApproveReaderViewModel model,
        CancellationToken cancellationToken = default)
    {
        if (!await IsLibrarianSignedInAsync(cancellationToken))
            return Unauthorized(new { message = "Thủ thư cần đăng nhập trước khi duyệt hồ sơ." });

        if (model == null)
            return BadRequest(new { message = "Thông tin cấp thẻ không hợp lệ." });

        model.ReaderAccountId = id;
        var outcome = await registrationService.ApproveReaderAsync(model, cancellationToken);
        if (!outcome.IsSuccess)
            return BadRequest(new { message = outcome.ErrorMessage });

        var card = outcome.LibraryCard!;
        await WriteIssueCardLogAsync(card, cancellationToken);
        return Ok(new
        {
            readerAccountId = card.ReaderAccountId,
            cardCode = card.CardCode,
            cardTypeId = card.LibraryCardTypeId,
            issuedOn = card.IssuedOn,
            expiresOn = card.ExpiresOn,
            status = card.Status
        });
    }

    [HttpPut("api/reader-registrations/{id}/reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectApi(
        int id,
        [FromBody] RejectReaderViewModel? model,
        CancellationToken cancellationToken = default)
    {
        if (!await IsLibrarianSignedInAsync(cancellationToken))
            return Unauthorized(new { message = "Thủ thư cần đăng nhập trước khi từ chối hồ sơ." });

        var outcome = await registrationService.RejectReaderAsync(id, model?.RejectionReason, cancellationToken);
        if (!outcome.IsSuccess)
            return BadRequest(new { message = outcome.ErrorMessage });

        return Ok(new { message = "Từ chối hồ sơ thành công.", id, status = "Từ chối" });
    }

    private async Task WriteIssueCardLogAsync(LibraryCard card, CancellationToken cancellationToken)
    {
        var staff = await auditLogService.GetSignedInStaffAsync(Request, cancellationToken);
        await auditLogService.WriteAsync(
            staff?.Email ?? "Không xác định",
            AuditActions.IssueCard,
            $"Thẻ {card.CardCode} – bạn đọc #{card.ReaderAccountId}",
            AuditLogService.ClientIp(HttpContext),
            cancellationToken);
    }

    private async Task<bool> IsLibrarianSignedInAsync(CancellationToken cancellationToken)
    {
        if (!Request.Cookies.TryGetValue("admin_refresh", out var token) || string.IsNullOrWhiteSpace(token))
            return false;

        var hash = TokenService.HashRefreshToken(token);
        return await dbContext.RefreshTokens.AnyAsync(item => item.TokenHash == hash &&
            item.RevokedAtUtc == null && item.ExpiresAtUtc > DateTime.UtcNow && item.AdminAccount.IsActive &&
            item.AdminAccount.Role == AccountRoles.Librarian,
            cancellationToken);
    }
}
