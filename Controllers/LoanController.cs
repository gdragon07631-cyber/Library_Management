using Project.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

[StaffOnly(AccountRoles.Librarian, AccountRoles.SystemAdmin, AccountRoles.LibraryManager)]
public sealed class LoanController(IBookLoanService loans, ApplicationDbContext db, IAuditLogService auditLog) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        if (!await IsLoanStaffSignedInAsync(ct)) return RedirectToAction("Login", "Account", new { returnUrl = Url.Action(nameof(Index)) });
        return View(await BuildModel(ct));
    }

    private async Task<bool> IsLoanStaffSignedInAsync(CancellationToken ct)
    {
        if (!Request.Cookies.TryGetValue("admin_refresh", out var token) || string.IsNullOrWhiteSpace(token)) return false;
        var hash = TokenService.HashRefreshToken(token);
        return await db.RefreshTokens.AnyAsync(item => item.TokenHash == hash &&
            item.RevokedAtUtc == null && item.ExpiresAtUtc > DateTime.UtcNow &&
            item.AdminAccount.IsActive && item.AdminAccount.Role != null &&
            (item.AdminAccount.Role == AccountRoles.Librarian ||
             item.AdminAccount.Role == AccountRoles.SystemAdmin ||
             item.AdminAccount.Role == AccountRoles.LibraryManager), ct);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateBookLoanViewModel model, CancellationToken ct = default)
    {
        if (!ModelState.IsValid || model.LoanDate == null)
        {
            TempData["ErrorMessage"] = ModelState.Values.SelectMany(value => value.Errors).FirstOrDefault()?.ErrorMessage ?? "Dữ liệu phiếu mượn không hợp lệ.";
            return View(nameof(Index), await BuildModel(ct));
        }

        var hasOverrideInput = model.OverrideBlocked || !string.IsNullOrWhiteSpace(model.OverrideReason);
        var staff = await auditLog.GetSignedInStaffAsync(Request, ct);
        Guid? overrideRequestId = null;
        if (hasOverrideInput)
        {
            if (staff?.Role != AccountRoles.LibraryManager) return Forbid();
            if (!model.OverrideBlocked || string.IsNullOrWhiteSpace(model.OverrideReason) ||
                !Guid.TryParse(model.OverrideRequestId, out var parsedRequestId))
            {
                TempData["ErrorMessage"] = "Vui lòng nhập lý do bỏ qua hợp lệ và xác nhận lại thao tác.";
                return RedirectToAction(nameof(Index));
            }
            overrideRequestId = parsedRequestId;
        }

        var result = await loans.CreateAsync(
            model.BookId, model.ReaderAccountId, model.LoanDate.Value, ct,
            staff is null ? await GetAuditActorAsync(ct) : DescribeStaff(staff),
            AuditLogService.ClientIp(HttpContext),
            hasOverrideInput ? model.OverrideReason : null,
            overrideRequestId,
            staff?.Role);
        TempData[result.IsSuccess ? "SuccessMessage" : "ErrorMessage"] = result.IsSuccess
            ? $"Đã tạo phiếu mượn. Hạn trả: {result.Loan!.DueDate:dd/MM/yyyy}."
            : result.ErrorMessage;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Renew(long id, CancellationToken ct = default)
    {
        var result = await loans.RenewAsync(id, DateOnly.FromDateTime(DateTime.Today), ct);
        TempData[result.IsSuccess ? "SuccessMessage" : "ErrorMessage"] = result.IsSuccess
            ? $"Đã gia hạn phiếu mượn. Hạn trả mới: {result.Loan!.DueDate:dd/MM/yyyy}. Số lần gia hạn: {result.Loan.RenewalCount} / {result.Loan.ReaderAccount!.LibraryCard!.LibraryCardType!.MaxRenewals}."
            : result.ErrorMessage;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Return(long id, CancellationToken ct = default)
    {
        var result = await loans.ReturnAsync(
            id, await GetAuditActorAsync(ct), AuditLogService.ClientIp(HttpContext), ct);
        TempData[result.IsSuccess ? "SuccessMessage" : "ErrorMessage"] = result.IsSuccess
            ? "Đã ghi nhận trả sách."
            : result.ErrorMessage;
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("api/loans")]
    public async Task<IActionResult> GetAllApi(CancellationToken ct = default) =>
        Ok((await loans.GetAllAsync(ct)).Select(ToApiModel));

    [HttpGet("api/loans/readers/{readerAccountId:int}/borrowing-status")]
    public async Task<IActionResult> GetBorrowingStatusApi(int readerAccountId, CancellationToken ct = default)
    {
        var status = await loans.GetBorrowingStatusAsync(readerAccountId, ct);
        if (status == null) return NotFound(new { message = "Không tìm thấy bạn đọc hoặc loại thẻ hợp lệ." });
        var staff = await auditLog.GetSignedInStaffAsync(Request, ct);
        return Ok(new
        {
            status.ReaderAccountId,
            status.ReaderCode,
            status.ReaderName,
            status.CardTypeName,
            status.BorrowedCount,
            status.BorrowingLimit,
            status.Remaining,
            status.CardStatus,
            cardExpiresOn = status.CardExpiresOn,
            status.OverdueLoanCount,
            status.OutstandingBalance,
            status.OutstandingBalanceFormatted,
            blockingReasons = status.BlockingReasons,
            status.IsBlocked,
            status.Message,
            canOverride = staff?.Role == AccountRoles.LibraryManager
        });
    }

    [HttpPost("api/loans")]
    public async Task<IActionResult> CreateApi([FromBody] CreateBookLoanViewModel? model, CancellationToken ct = default)
    {
        if (model?.LoanDate == null || !TryValidateModel(model))
            return BadRequest(new { message = "Dữ liệu phiếu mượn không hợp lệ." });

        var hasOverrideInput = model.OverrideBlocked || !string.IsNullOrWhiteSpace(model.OverrideReason);
        var staff = await auditLog.GetSignedInStaffAsync(Request, ct);
        Guid? overrideRequestId = null;
        if (hasOverrideInput)
        {
            if (staff?.Role != AccountRoles.LibraryManager)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "Chỉ quản lý thư viện được phép bỏ qua chặn." });
            if (!model.OverrideBlocked || string.IsNullOrWhiteSpace(model.OverrideReason) ||
                !Guid.TryParse(model.OverrideRequestId, out var parsedRequestId))
                return BadRequest(new { message = "Vui lòng nhập lý do bỏ qua hợp lệ và gửi mã yêu cầu." });
            overrideRequestId = parsedRequestId;
        }

        var result = await loans.CreateAsync(
            model.BookId, model.ReaderAccountId, model.LoanDate.Value, ct,
            staff is null ? await GetAuditActorAsync(ct) : DescribeStaff(staff),
            AuditLogService.ClientIp(HttpContext),
            hasOverrideInput ? model.OverrideReason : null,
            overrideRequestId,
            staff?.Role);
        if (!result.IsSuccess && result.ReasonCode is "LOAN_OVERRIDE_FORBIDDEN" or "INVALID_LOAN_OVERRIDE")
            return StatusCode(StatusCodes.Status403Forbidden, new { message = result.ErrorMessage, reason = result.ReasonCode });
        if (!result.IsSuccess && result.ReasonCode == "LOAN_OVERRIDE_ALREADY_PROCESSED")
            return Conflict(new { message = result.ErrorMessage, reason = result.ReasonCode });
        if (!result.IsSuccess && result.BlockingReasons is { Count: > 0 })
            return Conflict(new
            {
                message = result.ErrorMessage,
                reason = result.ReasonCode,
                borrowedCount = result.BorrowedCount,
                borrowingLimit = result.BorrowingLimit,
                outstandingBalance = result.OutstandingBalance,
                outstandingBalanceFormatted = result.OutstandingBalance is { } balance
                    ? ReaderBorrowingStatus.FormatVnd(balance)
                    : null,
                blockingReasons = result.BlockingReasons
            });
        if (!result.IsSuccess) return BadRequest(new { message = result.ErrorMessage });
        return Created($"/api/loans/{result.Loan!.Id}", ToApiModel(result.Loan));
    }

    [HttpPost("api/loans/{id:long}/renew")]
    public async Task<IActionResult> RenewApi(long id, CancellationToken ct = default)
    {
        var result = await loans.RenewAsync(id, DateOnly.FromDateTime(DateTime.Today), ct);
        if (!result.IsSuccess) return BadRequest(new { success = false, message = result.ErrorMessage, reason = result.ReasonCode });
        return Ok(new
        {
            success = true,
            message = "Gia hạn phiếu mượn thành công.",
            data = new
            {
                id = result.Loan!.Id,
                oldDueDate = result.OldDueDate,
                newDueDate = result.Loan.DueDate,
                renewalCount = result.Loan.RenewalCount,
                renewalLimit = result.Loan.ReaderAccount!.LibraryCard!.LibraryCardType!.MaxRenewals
            }
        });
    }

    [HttpPost("api/loans/{id:long}/return")]
    public async Task<IActionResult> ReturnApi(long id, CancellationToken ct = default)
    {
        var result = await loans.ReturnAsync(
            id, await GetAuditActorAsync(ct), AuditLogService.ClientIp(HttpContext), ct);
        if (!result.IsSuccess)
        {
            var statusCode = result.ErrorMessage == "Không tìm thấy phiếu mượn."
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status409Conflict;
            return StatusCode(statusCode, new { success = false, message = result.ErrorMessage });
        }

        return Ok(new
        {
            success = true,
            message = "Đã ghi nhận trả sách.",
            data = new { id = result.Loan!.Id, result.Loan.ReturnedAtUtc }
        });
    }

    [HttpGet("api/loans/adjust-due-date/{proposedDate}")]
    public async Task<IActionResult> PreviewDueDateApi(DateOnly proposedDate, CancellationToken ct = default)
    {
        try
        {
            var adjustedDate = await loans.AdjustDueDateAsync(proposedDate, ct);
            return Ok(new { proposedDate, adjustedDate, wasAdjusted = proposedDate != adjustedDate });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    private async Task<LoanIndexViewModel> BuildModel(CancellationToken ct) => new()
    {
        Loans = await loans.GetAllAsync(ct),
        Books = await db.Books.AsNoTracking().OrderBy(book => book.Title).ToListAsync(ct),
        Readers = await db.ReaderAccounts.AsNoTracking()
            .Where(reader => reader.Status == "Đang hoạt động").OrderBy(reader => reader.FullName).ToListAsync(ct),
        NewLoan = new CreateBookLoanViewModel
        {
            LoanDate = DateOnly.FromDateTime(DateTime.Today),
            OverrideRequestId = Guid.NewGuid().ToString()
        },
        CanOverrideLoanBlock =
            (await auditLog.GetSignedInStaffAsync(Request, ct))?.Role == AccountRoles.LibraryManager
    };

    private async Task<string?> GetAuditActorAsync(CancellationToken ct)
    {
        var staff = await auditLog.GetSignedInStaffAsync(Request, ct);
        return staff == null ? null : DescribeStaff(staff);
    }

    private static string DescribeStaff(AdminAccount staff) =>
        string.IsNullOrWhiteSpace(staff.FullName) ? staff.Email : staff.FullName;

    private static object ToApiModel(BookLoan loan) => new
    {
        loan.Id, loan.BookId, bookTitle = loan.Book?.Title, loan.ReaderAccountId,
        readerName = loan.ReaderAccount?.FullName, loan.LoanDate, loan.OriginalDueDate, loan.DueDate,
        renewalCount = loan.RenewalCount,
        renewalLimit = loan.ReaderAccount?.LibraryCard?.LibraryCardType?.MaxRenewals ?? 0,
        loan.ReturnedAtUtc,
        wasDueDateAdjusted = loan.OriginalDueDate != loan.DueDate,
        canRenew = loan.ReturnedAtUtc == null && loan.DueDate >= DateOnly.FromDateTime(DateTime.Today)
    };
}
