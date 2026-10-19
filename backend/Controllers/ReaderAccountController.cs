using Microsoft.AspNetCore.Mvc;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

/// <summary>
/// Quản trị hệ thống tra cứu và sửa mọi tài khoản bạn đọc để hỗ trợ ngay khi bạn đọc gặp sự cố:
/// sửa thông tin, xác nhận email, gửi lại email, gửi liên kết đặt lại mật khẩu, khoá/mở khoá, đăng xuất mọi thiết bị.
/// </summary>
public sealed class ReaderAccountController(
    IReaderAccountAdminService readerAccountService,
    IReaderEmailVerificationService emailVerificationService,
    IReaderPasswordResetService passwordResetService,
    IAuditLogService auditLogService,
    IWebHostEnvironment environment) : Controller
{
    private const int SearchLimit = 200;

    [HttpGet]
    public async Task<IActionResult> Index(string? q, string? status, CancellationToken cancellationToken = default)
    {
        var (_, denied) = await RequireCardManagerAsync(cancellationToken);
        if (denied is not null) return denied;

        ViewBag.Query = q;
        ViewBag.Status = status;
        ViewBag.Limit = SearchLimit;
        return View(await readerAccountService.SearchAsync(q, status, SearchLimit, cancellationToken));
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken = default)
    {
        var (_, denied) = await RequireCardManagerAsync(cancellationToken);
        if (denied is not null) return denied;

        var details = await readerAccountService.GetDetailsAsync(id, cancellationToken);
        return details is null ? NotFound("Không tìm thấy tài khoản bạn đọc.") : View(details);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind(Prefix = "Form")] ReaderAccountAdminFormViewModel model,
        CancellationToken cancellationToken = default)
    {
        var (admin, denied) = await RequireSystemAdminAsync(cancellationToken);
        if (denied is not null) return denied;
        model.Id = id;

        if (ModelState.IsValid)
        {
            var result = await readerAccountService.UpdateAsync(id, model, cancellationToken);
            if (result.Status == ReaderAccountAdminStatus.NotFound) return NotFound(result.ErrorMessage);
            if (result.IsSuccess)
            {
                await WriteLogAsync(admin!.Email, AuditActions.UpdateAccount, result.Reader!,
                    "quản trị sửa " + string.Join("; ", result.Changes!), cancellationToken);
                TempData["SuccessMessage"] = "Đã lưu thông tin bạn đọc.";
                return RedirectToAction(nameof(Details), new { id });
            }
            if (result.Status == ReaderAccountAdminStatus.NoChange)
            {
                TempData["InfoMessage"] = result.ErrorMessage;
                return RedirectToAction(nameof(Details), new { id });
            }

            var field = result.Status == ReaderAccountAdminStatus.DuplicateCode
                ? nameof(model.StudentOrStaffCode)
                : nameof(model.Email);
            ModelState.AddModelError("Form." + field, result.ErrorMessage!);
        }

        // Hiển thị lại trang chi tiết với dữ liệu vừa nhập và thông báo lỗi.
        var details = await readerAccountService.GetDetailsAsync(id, cancellationToken);
        if (details is null) return NotFound("Không tìm thấy tài khoản bạn đọc.");
        return View(nameof(Details), new ReaderAccountAdminDetailsViewModel
        {
            Reader = details.Reader,
            Form = model,
            ActiveHolds = details.ActiveHolds,
            History = details.History
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetLocked(int id, bool locked, string? reason, CancellationToken cancellationToken = default)
    {
        var (admin, denied) = await RequireSystemAdminAsync(cancellationToken);
        if (denied is not null) return denied;

        var result = await readerAccountService.SetLockedAsync(id, locked, reason, cancellationToken);
        if (result.Status == ReaderAccountAdminStatus.NotFound) return NotFound(result.ErrorMessage);
        if (!result.IsSuccess)
        {
            TempData["ErrorMessage"] = result.ErrorMessage;
            return RedirectToAction(nameof(Details), new { id });
        }

        await WriteLogAsync(admin!.Email, AuditActions.UpdateAccount, result.Reader!,
            locked ? $"khoá tài khoản, lý do: {result.Reader!.LockReason}" : "mở khoá tài khoản", cancellationToken);
        TempData["SuccessMessage"] = locked
            ? "Đã khoá tài khoản. Bạn đọc bị đăng xuất khỏi mọi thiết bị và không thể đăng nhập cho tới khi được mở khoá."
            : "Đã mở khoá tài khoản. Bạn đọc có thể đăng nhập lại.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetCardLocked(int id, string? reason, string? lockType, DateTime? lockExpiresAt, string? returnUrl = null, CancellationToken cancellationToken = default)
    {
        var (admin, denied) = await RequireCardManagerAsync(cancellationToken);
        if (denied is not null) return denied;

        var details = await readerAccountService.GetDetailsAsync(id, cancellationToken);
        if (details is null) return NotFound("Không tìm thấy tài khoản bạn đọc.");

        // Khoá từ danh sách đề xuất: mọi kết quả đều quay lại danh sách kèm thông báo thay vì trang chi tiết.
        var backToList = IsLocalPath(returnUrl);
        IActionResult Back() => backToList ? LocalRedirect(returnUrl!) : RedirectToAction(nameof(Details), new { id });

        var card = details.Reader.LibraryCard;
        if (card is null)
        {
            TempData["ErrorMessage"] = "Bạn đọc chưa được cấp thẻ thư viện để khoá.";
            return Back();
        }

        var useManualUnlock = string.Equals(lockType, "manual", StringComparison.OrdinalIgnoreCase);
        var useTimedLock = string.Equals(lockType, "temporary", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(reason))
        {
            ModelState.AddModelError(nameof(reason), "Vui lòng nhập lý do khoá thẻ.");
        }

        if (!useManualUnlock && !useTimedLock)
            ModelState.AddModelError(nameof(lockType), "Vui lòng chọn hình thức khoá hợp lệ.");

        DateTime? lockExpiresAtUtc = null;
        if (useTimedLock && !lockExpiresAt.HasValue)
        {
            ModelState.AddModelError(nameof(lockExpiresAt), "Vui lòng chọn ngày và giờ hết hạn khoá.");
        }
        else if (useTimedLock && lockExpiresAt.HasValue)
        {
            var localExpiry = DateTime.SpecifyKind(lockExpiresAt.Value, DateTimeKind.Unspecified);
            if (TimeZoneInfo.Local.IsInvalidTime(localExpiry))
                ModelState.AddModelError(nameof(lockExpiresAt), "Ngày và giờ hết hạn không hợp lệ theo múi giờ hiện tại.");
            else
            {
                lockExpiresAtUtc = TimeZoneInfo.ConvertTimeToUtc(localExpiry, TimeZoneInfo.Local);
                if (lockExpiresAtUtc.Value <= DateTime.UtcNow)
                    ModelState.AddModelError(nameof(lockExpiresAt), "Thời hạn khoá phải sau thời điểm bắt đầu khoá.");
            }
        }

        if (!ModelState.IsValid)
        {
            if (!backToList) return View(nameof(Details), details);
            TempData["ErrorMessage"] = $"Chưa khoá thẻ {card.CardCode}: " + string.Join(" ",
                ModelState.Values.SelectMany(value => value.Errors).Select(error => error.ErrorMessage));
            return Back();
        }

        var result = await readerAccountService.SetCardLockedAsync(id, reason, useManualUnlock ? null : lockExpiresAtUtc, useManualUnlock, admin!.Id, cancellationToken);
        if (!result.IsSuccess)
        {
            if (result.Status == ReaderAccountAdminStatus.NoChange)
            {
                TempData["ErrorMessage"] = result.ErrorMessage ?? "Thẻ hiện không thể khoá lại.";
                return Back();
            }
            if (backToList)
            {
                TempData["ErrorMessage"] = result.ErrorMessage ?? "Không thể khoá thẻ.";
                return Back();
            }
            ModelState.AddModelError(string.Empty, result.ErrorMessage ?? "Không thể khoá thẻ.");
            return View(nameof(Details), details);
        }

        await WriteLogAsync(admin.Email, AuditActions.LockCard, details.Reader,
            $"khoá thẻ {card.CardCode}, lý do: {reason!.Trim()}{(useManualUnlock ? ", đến khi mở khoá thủ công" : $", hết hạn {lockExpiresAtUtc!.Value.ToLocalTime():dd/MM/yyyy HH:mm}")}", cancellationToken);
        TempData["SuccessMessage"] = useManualUnlock
            ? $"Đã khoá thẻ {card.CardCode}. Thẻ sẽ tiếp tục bị khoá đến khi được mở thủ công."
            : $"Đã khoá thẻ {card.CardCode}. Thẻ sẽ bị khoá tới {lockExpiresAtUtc!.Value.ToLocalTime():dd/MM/yyyy HH:mm}.";
        return Back();
    }

    /// <summary>Chỉ chấp nhận đường dẫn nội bộ ("/..."), chặn chuyển hướng ra ngoài ("//host", "/\host").</summary>
    private static bool IsLocalPath(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetCardUnlocked(int id, string? reason, CancellationToken cancellationToken = default)
    {
        // Cùng quyền với khoá thẻ (Lát 1): Quản lý thư viện hoặc Quản trị hệ thống; người thực hiện lấy từ phiên đăng nhập.
        var (admin, denied) = await RequireCardManagerAsync(cancellationToken);
        if (denied is not null) return denied;

        var details = await readerAccountService.GetDetailsAsync(id, cancellationToken);
        if (details is null) return NotFound("Không tìm thấy tài khoản bạn đọc.");

        var card = details.Reader.LibraryCard;
        if (card is null)
        {
            TempData["ErrorMessage"] = "Bạn đọc chưa được cấp thẻ thư viện để mở khoá.";
            return RedirectToAction(nameof(Details), new { id });
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["ErrorMessage"] = "Vui lòng nhập lý do mở khoá thẻ.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var result = await readerAccountService.SetCardUnlockedAsync(
            id,
            reason,
            admin!.Id,
            admin.Email,
            AuditLogService.ClientIp(HttpContext),
            cancellationToken);

        if (!result.IsSuccess)
        {
            TempData["ErrorMessage"] = result.ErrorMessage ?? "Không thể mở khoá thẻ.";
            return RedirectToAction(nameof(Details), new { id });
        }

        TempData["SuccessMessage"] = $"Đã mở khoá thẻ {card.CardCode}. Thẻ quay lại trạng thái hoạt động.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private const int CardLockHistoryLimit = 500;

    /// <summary>Lịch sử khoá/mở khoá thẻ: tìm theo mã thẻ hoặc tên bạn đọc, lọc theo khoảng ngày và loại sự kiện.</summary>
    [HttpGet]
    public async Task<IActionResult> CardLockHistory([FromQuery] LibraryCardLockHistoryFilter filter, CancellationToken cancellationToken = default)
    {
        var (_, denied) = await RequireCardManagerAsync(cancellationToken);
        if (denied is not null) return denied;

        var error = ValidateHistoryFilter(filter);
        return View(new LibraryCardLockHistoryPageViewModel
        {
            Filter = filter,
            Limit = CardLockHistoryLimit,
            ErrorMessage = error,
            Entries = error is null ? await readerAccountService.SearchCardLockHistoryAsync(filter, CardLockHistoryLimit, cancellationToken) : []
        });
    }

    [HttpGet]
    public async Task<IActionResult> CardLockHistoryDetails(long id, CancellationToken cancellationToken = default)
    {
        var (_, denied) = await RequireCardManagerAsync(cancellationToken);
        if (denied is not null) return denied;

        var entry = await readerAccountService.GetCardLockHistoryAsync(id, cancellationToken);
        return entry is null ? NotFound("Không tìm thấy bản ghi nhật ký khoá/mở khoá thẻ.") : View(entry);
    }

    [HttpGet("api/card-lock-history")]
    public async Task<IActionResult> CardLockHistoryApi([FromQuery] LibraryCardLockHistoryFilter filter, CancellationToken cancellationToken = default)
    {
        if (await RequireCardManagerApiAsync(cancellationToken) is { } denied) return denied;

        var error = ValidateHistoryFilter(filter);
        if (error is not null) return BadRequest(new { message = error });
        return Ok(await readerAccountService.SearchCardLockHistoryAsync(filter, CardLockHistoryLimit, cancellationToken));
    }

    [HttpGet("api/card-lock-history/{id:long}")]
    public async Task<IActionResult> CardLockHistoryEntryApi(long id, CancellationToken cancellationToken = default)
    {
        if (await RequireCardManagerApiAsync(cancellationToken) is { } denied) return denied;

        var entry = await readerAccountService.GetCardLockHistoryAsync(id, cancellationToken);
        return entry is null ? NotFound(new { message = "Không tìm thấy bản ghi nhật ký khoá/mở khoá thẻ." }) : Ok(entry);
    }

    private static string? ValidateHistoryFilter(LibraryCardLockHistoryFilter filter)
    {
        if (filter.FromDate is { } from && filter.ToDate is { } to && from > to)
            return "Từ ngày phải trước hoặc bằng đến ngày.";
        if (!string.IsNullOrWhiteSpace(filter.EventType) && !LibraryCardLockEvents.All.Contains(filter.EventType))
            return "Loại sự kiện không hợp lệ.";
        return null;
    }

    /// <summary>API trả JSON 401/403 thay vì chuyển trang đăng nhập.</summary>
    private async Task<IActionResult?> RequireCardManagerApiAsync(CancellationToken cancellationToken)
    {
        var staff = await auditLogService.GetSignedInStaffAsync(Request, cancellationToken);
        if (staff is null) return Unauthorized(new { message = "Vui lòng đăng nhập tài khoản nhân sự." });
        return staff.Role is AccountRoles.SystemAdmin or AccountRoles.LibraryManager
            ? null
            : StatusCode(StatusCodes.Status403Forbidden, new { message = "Chỉ Quản lý thư viện hoặc Quản trị hệ thống được xem nhật ký khoá/mở khoá thẻ." });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeSessions(int id, CancellationToken cancellationToken = default)
    {
        var (admin, denied) = await RequireSystemAdminAsync(cancellationToken);
        if (denied is not null) return denied;

        var result = await readerAccountService.RevokeSessionsAsync(id, cancellationToken);
        if (result.Status == ReaderAccountAdminStatus.NotFound) return NotFound(result.ErrorMessage);

        await WriteLogAsync(admin!.Email, AuditActions.UpdateAccount, result.Reader!, "đăng xuất khỏi mọi thiết bị", cancellationToken);
        TempData["SuccessMessage"] = "Đã đăng xuất bạn đọc khỏi mọi thiết bị.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmEmail(int id, CancellationToken cancellationToken = default)
    {
        var (admin, denied) = await RequireSystemAdminAsync(cancellationToken);
        if (denied is not null) return denied;

        var details = await readerAccountService.GetDetailsAsync(id, cancellationToken);
        if (details is null) return NotFound("Không tìm thấy tài khoản bạn đọc.");

        var outcome = await emailVerificationService.ConfirmByAdminAsync(id, cancellationToken);
        if (outcome.Result == EmailConfirmationResult.AlreadyConfirmed)
        {
            TempData["InfoMessage"] = "Email của bạn đọc đã được xác nhận từ trước.";
            return RedirectToAction(nameof(Details), new { id });
        }

        await WriteLogAsync(admin!.Email, AuditActions.UpdateAccount, details.Reader, "quản trị xác nhận email thủ công", cancellationToken);
        if (outcome.IssuedCard is { } card)
        {
            await auditLogService.WriteAsync(admin.Email, AuditActions.IssueCard,
                $"Thẻ {card.CardCode} – bạn đọc #{id} (tự động khi quản trị xác nhận email)",
                AuditLogService.ClientIp(HttpContext), cancellationToken);
        }
        TempData["SuccessMessage"] = outcome.IssuedCard is { } issued
            ? $"Đã xác nhận email. Tài khoản đã được kích hoạt và cấp thẻ {issued.CardCode} (hạn {issued.ExpiresOn:dd/MM/yyyy})."
            : "Đã xác nhận email. Bạn đọc có thể đăng nhập ngay.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResendConfirmation(int id, CancellationToken cancellationToken = default)
    {
        var (admin, denied) = await RequireSystemAdminAsync(cancellationToken);
        if (denied is not null) return denied;

        var details = await readerAccountService.GetDetailsAsync(id, cancellationToken);
        if (details is null) return NotFound("Không tìm thấy tài khoản bạn đọc.");
        if (details.Reader.EmailConfirmed)
        {
            TempData["InfoMessage"] = "Email của bạn đọc đã được xác nhận, không cần gửi lại.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var result = await emailVerificationService.SendAsync(details.Reader, ReaderUrl("ConfirmEmail"), cancellationToken);
        await WriteLogAsync(admin!.Email, AuditActions.UpdateAccount, details.Reader, "quản trị gửi lại email xác nhận", cancellationToken);
        ReportEmail(result.Sent, result.Link, $"Đã gửi lại email xác nhận tới {details.Reader.Email}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendPasswordReset(int id, CancellationToken cancellationToken = default)
    {
        var (admin, denied) = await RequireSystemAdminAsync(cancellationToken);
        if (denied is not null) return denied;

        var details = await readerAccountService.GetDetailsAsync(id, cancellationToken);
        if (details is null) return NotFound("Không tìm thấy tài khoản bạn đọc.");

        // Quản trị không đặt hay xem mật khẩu của bạn đọc: chỉ gửi liên kết để bạn đọc tự đặt mật khẩu mới.
        var accepted = await passwordResetService.RequestAsync(details.Reader.Email, ReaderUrl("ResetPassword"), cancellationToken);
        if (!accepted)
        {
            TempData["ErrorMessage"] = "Email này đã yêu cầu đặt lại mật khẩu quá nhiều lần gần đây. Vui lòng thử lại sau.";
            return RedirectToAction(nameof(Details), new { id });
        }

        await WriteLogAsync(admin!.Email, AuditActions.ResetPassword, details.Reader, "quản trị gửi liên kết đặt lại mật khẩu", cancellationToken);
        TempData["SuccessMessage"] =
            $"Đã gửi liên kết đặt lại mật khẩu (hiệu lực 30 phút) tới {details.Reader.Email}. Nếu bạn đọc không thấy, nhắc kiểm tra thư mục Spam.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private Task<(AdminAccount? Admin, IActionResult? Denied)> RequireSystemAdminAsync(CancellationToken cancellationToken) =>
        RequireRoleAsync(cancellationToken, AccountRoles.SystemAdmin);

    /// <summary>Xem danh sách/chi tiết bạn đọc và khoá thẻ: Quản lý thư viện hoặc Quản trị hệ thống.</summary>
    private Task<(AdminAccount? Admin, IActionResult? Denied)> RequireCardManagerAsync(CancellationToken cancellationToken) =>
        RequireRoleAsync(cancellationToken, AccountRoles.SystemAdmin, AccountRoles.LibraryManager);

    private async Task<(AdminAccount? Admin, IActionResult? Denied)> RequireRoleAsync(CancellationToken cancellationToken, params string[] roles)
    {
        var staff = await auditLogService.GetSignedInStaffAsync(Request, cancellationToken);
        if (staff is null)
            return (null, RedirectToAction("Login", "Account", new { returnUrl = Request.Path.Value }));
        if (!roles.Contains(staff.Role))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return (null, View("AccessDenied"));
        }
        return (staff, null);
    }

    private void ReportEmail(bool sent, string? link, string successMessage)
    {
        if (sent)
        {
            TempData["SuccessMessage"] = successMessage;
            return;
        }
        TempData["WarningMessage"] = "Chưa gửi được email. Kiểm tra cấu hình SMTP hoặc dùng nút \"Xác nhận email thủ công\".";
        if (environment.IsDevelopment()) TempData["DevLink"] = link;
    }

    private string ReaderUrl(string action) => Url.Action(action, "ReaderRegistration", null, Request.Scheme)!;

    private Task WriteLogAsync(string actor, string action, ReaderAccount reader, string detail, CancellationToken cancellationToken) =>
        auditLogService.WriteAsync(
            actor,
            action,
            $"Tài khoản bạn đọc #{reader.Id} ({reader.Email}) – {detail}",
            AuditLogService.ClientIp(HttpContext),
            cancellationToken);
}
