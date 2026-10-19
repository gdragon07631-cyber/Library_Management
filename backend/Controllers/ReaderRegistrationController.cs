using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

public sealed class ReaderRegistrationController(
    IReaderRegistrationService registrationService,
    ReaderRegistrationIpRateLimiter ipRateLimiter,
    IReaderPasswordResetService passwordResetService,
    IDataProtectionProvider dataProtectionProvider,
    IAuditLogService auditLogService,
    IReaderEmailVerificationService emailVerificationService,
    IHoldPickupConfirmationService? holdPickupConfirmationService = null) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Register(CancellationToken cancellationToken = default) =>
        await RegisterViewAsync(new ReaderRegistrationViewModel(), cancellationToken);

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(
        ReaderRegistrationViewModel model,
        CancellationToken cancellationToken = default)
    {
        // Keep date binding errors next to the field, in the form's language.
        if (ModelState.TryGetValue(nameof(model.DateOfBirth), out var dateState) &&
            dateState.Errors.Count > 0 && !string.IsNullOrWhiteSpace(dateState.AttemptedValue))
        {
            dateState.Errors.Clear();
            ModelState.AddModelError(nameof(model.DateOfBirth), "Ngày sinh không hợp lệ.");
        }

        // Loại thẻ phải là loại đang hoạt động; thẻ được cấp tự động khi xác nhận email.
        if (model.LibraryCardTypeId is { } cardTypeId &&
            !(await registrationService.GetActiveCardTypesAsync(cancellationToken)).Any(type => type.Id == cardTypeId))
        {
            ModelState.AddModelError(nameof(model.LibraryCardTypeId), "Loại thẻ không tồn tại hoặc đã ngừng sử dụng.");
        }

        if (!ModelState.IsValid)
        {
            return await RegisterViewAsync(model, cancellationToken);
        }

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!ipRateLimiter.TryAcquire(ipAddress, out var registrationLease))
        {
            ModelState.AddModelError(string.Empty,
                "Địa chỉ IP này đã đạt giới hạn 3 lượt đăng ký trong một giờ. Vui lòng thử lại sau.");
            return await RegisterViewAsync(model, cancellationToken);
        }

        using (registrationLease)
        {
        var outcome = await registrationService.RegisterAsync(model, cancellationToken);

        if (!outcome.IsSuccess)
        {
            // 3 & 5. Hiển thị lỗi ngay trên biểu mẫu khi email đã được đăng ký kèm gợi ý "Quên mật khẩu"
            if (outcome.IsEmailDuplicate)
            {
                ModelState.AddModelError(
                    nameof(model.Email),
                    "Email này đã được đăng ký. Nếu đây là tài khoản của bạn, hãy sử dụng chức năng Quên mật khẩu.");
                ViewBag.ShowForgotPasswordSuggestion = true;
            }

            // 4. Hiển thị lỗi ngay trên biểu mẫu khi mã sinh viên hoặc mã cán bộ đã được đăng ký
            if (outcome.IsCodeDuplicate)
            {
                ModelState.AddModelError(
                    nameof(model.StudentOrStaffCode),
                    "Mã sinh viên/mã cán bộ này đã được đăng ký. Nếu đây là tài khoản của bạn, hãy sử dụng chức năng Quên mật khẩu.");
                ViewBag.ShowCodeForgotPasswordSuggestion = true;
            }

            // 8. Tài khoản bị từ chối do trùng email hoặc trùng mã KHÔNG được tạo thêm bản ghi mới
            return await RegisterViewAsync(model, cancellationToken);
        }

        registrationLease.Commit();

        var account = outcome.Account!;
        var staff = await auditLogService.GetSignedInStaffAsync(Request, cancellationToken);
        await auditLogService.WriteAsync(
            staff?.Email ?? account.Email,
            AuditActions.CreateAccount,
            $"Tài khoản bạn đọc #{account.Id} ({account.Email})",
            AuditLogService.ClientIp(HttpContext),
            cancellationToken);

        var verification = await emailVerificationService.SendAsync(account, ConfirmEmailUrl(), cancellationToken);
        ReportVerificationEmail(account.Email, verification);

        // Không cần thủ thư duyệt: tài khoản được kích hoạt và cấp thẻ ngay khi xác nhận email.
        TempData["SuccessMessage"] = "Đăng ký tài khoản thành công!";
        TempData["AccountStatus"] = "Chờ xác nhận email";
        return RedirectToAction(nameof(RegisterSuccess));
        }
    }

    [HttpGet]
    public IActionResult RegisterSuccess()
    {
        if (TempData["SuccessMessage"] == null)
        {
            return RedirectToAction(nameof(Register));
        }

        ViewBag.SuccessMessage = TempData["SuccessMessage"];
        ViewBag.AccountStatus = TempData["AccountStatus"];
        ViewBag.VerificationEmail = TempData["VerificationEmail"];
        ViewBag.VerificationEmailSent = TempData["VerificationEmailSent"];
        ViewBag.DevConfirmLink = TempData["DevConfirmLink"];
        ViewBag.Instruction = "Ngay khi bạn bấm liên kết xác nhận trong email, tài khoản được kích hoạt và thẻ thư viện được cấp tự động (hạn 1 năm). Bạn có thể đăng nhập, tra cứu và đặt giữ sách ngay, không cần chờ thư viện duyệt.";
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> Profile(int? id, CancellationToken cancellationToken = default)
    {
        // The profile always belongs to the signed-in reader; an id in the URL must match that session.
        int targetId = await GetCurrentLoggedInReaderIdAsync(cancellationToken);
        if (targetId <= 0)
        {
            return RedirectToAction(nameof(Login));
        }
        if (id.HasValue && id.Value != targetId)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Bạn không có quyền xem hồ sơ của bạn đọc khác.");
        }

        var reader = await registrationService.GetReaderByIdAsync(targetId, cancellationToken);
        if (reader == null)
        {
            return NotFound("Không tìm thấy thông tin tài khoản Bạn đọc.");
        }

        return View(ToProfileViewModel(reader));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Profile(
        ReaderProfileViewModel model, CancellationToken cancellationToken = default)
    {
        int readerId = await GetCurrentLoggedInReaderIdAsync(cancellationToken);
        if (readerId <= 0) return RedirectToAction(nameof(Login));

        var current = await registrationService.GetReaderByIdAsync(readerId, cancellationToken);
        if (current == null) return NotFound("Không tìm thấy thông tin tài khoản Bạn đọc.");

        if (!ModelState.IsValid)
        {
            CopyFixedProfileFields(model, current);
            model.CurrentPassword = string.Empty;
            ModelState.Remove(nameof(model.CurrentPassword));
            return View(model);
        }

        var emailChanged = !string.Equals(current.Email.Trim(), model.Email.Trim(), StringComparison.OrdinalIgnoreCase);
        if (emailChanged && string.IsNullOrWhiteSpace(model.CurrentPassword))
        {
            ModelState.AddModelError(nameof(model.CurrentPassword), "Vui lòng nhập mật khẩu hiện tại.");
            CopyFixedProfileFields(model, current);
            model.CurrentPassword = string.Empty;
            ModelState.SetModelValue(nameof(model.CurrentPassword), string.Empty, string.Empty);
            return View(model);
        }

        var updateResult = await registrationService.UpdateReaderContactAsync(
            readerId, model.PhoneNumber, model.Address, model.Email, model.CurrentPassword, cancellationToken);
        if (updateResult == ReaderContactUpdateResult.NotFound)
            return NotFound("Không tìm thấy thông tin tài khoản Bạn đọc.");
        if (updateResult == ReaderContactUpdateResult.InvalidCurrentPassword)
        {
            ModelState.AddModelError(nameof(model.CurrentPassword), "Mật khẩu hiện tại không chính xác.");
            CopyFixedProfileFields(model, current);
            model.CurrentPassword = string.Empty;
            ModelState.SetModelValue(nameof(model.CurrentPassword), string.Empty, string.Empty);
            return View(model);
        }
        if (updateResult == ReaderContactUpdateResult.EmailInUse)
        {
            ModelState.AddModelError(nameof(model.Email), "Email này đã được tài khoản khác sử dụng.");
            CopyFixedProfileFields(model, current);
            model.CurrentPassword = string.Empty;
            ModelState.SetModelValue(nameof(model.CurrentPassword), string.Empty, string.Empty);
            return View(model);
        }

        await auditLogService.WriteAsync(
            current.Email,
            AuditActions.UpdateAccount,
            emailChanged
                ? $"Tài khoản bạn đọc #{readerId} (yêu cầu đổi email {current.Email} → {model.Email.Trim()}, chờ xác nhận)"
                : $"Tài khoản bạn đọc #{readerId} ({current.Email})",
            AuditLogService.ClientIp(HttpContext),
            cancellationToken);

        if (updateResult == ReaderContactUpdateResult.EmailChangePending)
        {
            // Email chỉ đổi khi chủ địa chỉ mới bấm liên kết xác nhận; đến lúc đó vẫn đăng nhập bằng email cũ.
            var updated = await registrationService.GetReaderByIdAsync(readerId, cancellationToken);
            var verification = await emailVerificationService.SendEmailChangeAsync(updated!, ConfirmEmailUrl(), cancellationToken);
            TempData["ProfileSuccessMessage"] = verification.Throttled
                ? "Đã lưu thông tin. Bạn đã yêu cầu đổi email quá nhiều lần trong một giờ, vui lòng thử lại sau."
                : $"Đã lưu thông tin. Chúng tôi đã gửi liên kết xác nhận tới {model.Email.Trim()}. " +
                  "Email đăng nhập chỉ được đổi sau khi bạn bấm liên kết đó (hiệu lực 24 giờ); trước đó vẫn dùng email cũ.";
            if (!verification.Sent && !verification.Throttled && IsDevelopment())
                TempData["DevConfirmLink"] = verification.Link;
            return RedirectToAction(nameof(Profile));
        }

        TempData["ProfileSuccessMessage"] = "Lưu thông tin thành công.";
        return RedirectToAction(nameof(Profile));
    }

    private static ReaderProfileViewModel ToProfileViewModel(ReaderAccount reader) => new()
    {
        FullName = reader.FullName,
        DateOfBirth = reader.DateOfBirth,
        StudentOrStaffCode = reader.StudentOrStaffCode,
        Status = reader.Status,
        RejectionReason = reader.RejectionReason,
        PhoneNumber = reader.PhoneNumber,
        Address = reader.Address ?? string.Empty,
        Email = reader.Email,
        PendingEmail = reader.PendingEmail,
        CardCode = reader.LibraryCard?.CardCode,
        CardType = reader.LibraryCard?.LibraryCardType?.Name,
        CardExpiresOn = reader.LibraryCard?.ExpiresOn,
        CardStatus = ResolveCardStatus(reader.LibraryCard),
        LibraryCard = reader.LibraryCard
    };

    private static void CopyFixedProfileFields(ReaderProfileViewModel model, ReaderAccount reader)
    {
        model.FullName = reader.FullName;
        model.DateOfBirth = reader.DateOfBirth;
        model.StudentOrStaffCode = reader.StudentOrStaffCode;
        model.Status = reader.Status;
        model.RejectionReason = reader.RejectionReason;
        model.CardCode = reader.LibraryCard?.CardCode;
        model.CardType = reader.LibraryCard?.LibraryCardType?.Name;
        model.CardExpiresOn = reader.LibraryCard?.ExpiresOn;
        model.CardStatus = ResolveCardStatus(reader.LibraryCard);
        model.LibraryCard = reader.LibraryCard;
        model.PendingEmail = reader.PendingEmail;
    }

    private static string ResolveCardStatus(LibraryCard? card)
    {
        if (card is null)
        {
            return "Chưa có thẻ";
        }

        if (LibraryCardStatusHelper.IsCardLocked(card))
            return "Đang bị khoá";

        // Khoá có thời hạn đã hết hạn được coi là đang hoạt động, đúng như khi kiểm tra mượn/đặt giữ/gia hạn.
        var status = LibraryCardStatusHelper.GetEffectiveStatus(card);
        return string.IsNullOrWhiteSpace(status) ? "Đang hoạt động" : status.Trim();
    }

    /// <summary>EF đọc datetime2 với Kind = Unspecified; đánh dấu UTC để JSON có hậu tố "Z" và trình duyệt đổi đúng giờ.</summary>
    private static DateTime? AsUtc(DateTime? value) =>
        value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(
        ReaderChangePasswordViewModel model, CancellationToken cancellationToken = default)
    {
        int readerId = await GetCurrentLoggedInReaderIdAsync(cancellationToken);
        if (readerId <= 0) return RedirectToAction(nameof(Login));

        if (!ModelState.IsValid)
        {
            TempData["PasswordErrorMessage"] = ModelState.Values
                .SelectMany(entry => entry.Errors)
                .Select(error => error.ErrorMessage)
                .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message))
                ?? "Vui lòng kiểm tra lại thông tin mật khẩu.";
            return RedirectToAction(nameof(Profile));
        }

        var result = await registrationService.ChangeReaderPasswordAsync(
            readerId, model.CurrentPassword, model.NewPassword, cancellationToken);
        switch (result)
        {
            case ReaderPasswordChangeResult.NotFound:
                return NotFound("Không tìm thấy thông tin tài khoản Bạn đọc.");
            case ReaderPasswordChangeResult.IncorrectCurrentPassword:
                TempData["PasswordErrorMessage"] = "Mật khẩu cũ không chính xác.";
                await LogReaderActivityAsync(readerId, AuditActions.ChangePassword, "thất bại: mật khẩu cũ không chính xác", cancellationToken);
                break;
            case ReaderPasswordChangeResult.PasswordRecentlyUsed:
                TempData["PasswordErrorMessage"] = "Mật khẩu mới không được trùng với 3 mật khẩu gần nhất.";
                await LogReaderActivityAsync(readerId, AuditActions.ChangePassword, "thất bại: trùng 3 mật khẩu gần nhất", cancellationToken);
                break;
            case ReaderPasswordChangeResult.Success:
                TempData["PasswordSuccessMessage"] = "Đổi mật khẩu thành công.";
                await LogReaderActivityAsync(readerId, AuditActions.ChangePassword, "đổi mật khẩu thành công", cancellationToken);
                break;
        }

        return RedirectToAction(nameof(Profile));
    }

    [HttpGet("api/reader/profile")]
    public async Task<IActionResult> GetProfileApi(CancellationToken cancellationToken = default)
    {
        var readerId = await GetCurrentLoggedInReaderIdAsync(cancellationToken);
        if (readerId <= 0)
            return Unauthorized(new { message = "Vui lòng đăng nhập trước khi xem thông tin cá nhân." });

        var reader = await registrationService.GetReaderByIdAsync(readerId, cancellationToken);
        if (reader == null)
            return NotFound(new { message = "Không tìm thấy thông tin tài khoản Bạn đọc." });

        var card = reader.LibraryCard;
        return Ok(new
        {
            id = reader.Id,
            fullName = reader.FullName,
            studentOrStaffCode = reader.StudentOrStaffCode,
            status = reader.Status,
            rejectionReason = reader.RejectionReason,
            libraryCard = new
            {
                id = card?.Id,
                code = card?.CardCode,
                type = card?.LibraryCardType?.Name,
                status = ResolveCardStatus(card),
                isLocked = card is not null && LibraryCardStatusHelper.IsCardLocked(card),
                lockReason = card?.LockReason,
                lockedAtUtc = AsUtc(card?.LockedAtUtc),
                lockExpiresAtUtc = AsUtc(card?.LockExpiresAtUtc),
                isLockUntilManualUnlock = card?.IsLockUntilManualUnlock ?? false,
                lockMessage = card is null ? null : LibraryCardStatusHelper.GetLockMessage(card)
            }
        });
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [HttpGet]
    public IActionResult ForgotPassword() => View(new ForgotPasswordViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model, CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid) return View(model);
        var resetUrl = Url.Action(nameof(ResetPassword), "ReaderRegistration", null, Request.Scheme);
        var requestAccepted = await passwordResetService.RequestAsync(model.Email, resetUrl!, cancellationToken);
        await auditLogService.WriteAsync(
            model.Email.Trim(),
            AuditActions.ResetPassword,
            $"Yêu cầu liên kết đặt lại mật khẩu cho email {model.Email.Trim()}" +
                (requestAccepted ? string.Empty : " – bị từ chối do gửi quá nhiều lần"),
            AuditLogService.ClientIp(HttpContext),
            cancellationToken);
        ViewBag.Message = requestAccepted
            ? "Nếu email đã đăng ký, hướng dẫn đặt lại mật khẩu sẽ được gửi đến hộp thư của bạn."
            : "Bạn đã gửi quá nhiều yêu cầu trong thời gian ngắn. Vui lòng thử lại sau.";
        return View(new ForgotPasswordViewModel());
    }

    [HttpGet]
    public async Task<IActionResult> ResetPassword(string? token, CancellationToken cancellationToken = default)
    {
        if (!await passwordResetService.IsTokenValidAsync(token ?? string.Empty, cancellationToken))
        {
            ViewBag.InvalidToken = true;
            return View(new ResetPasswordViewModel());
        }
        return View(new ResetPasswordViewModel { Token = token! });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model, CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid) return View(model);
        var resetReaderId = await passwordResetService.GetReaderIdForTokenAsync(model.Token, cancellationToken);
        if (!await passwordResetService.ResetAsync(model.Token, model.Password, cancellationToken))
        {
            ViewBag.InvalidToken = true;
            return View(model);
        }
        if (resetReaderId is { } readerId)
            await LogReaderActivityAsync(readerId, AuditActions.ResetPassword,
                "đặt lại mật khẩu qua email thành công, mọi phiên đăng nhập cũ bị thu hồi", cancellationToken);
        ViewBag.Success = "Mật khẩu đã được cập nhật thành công.";
        return View(new ResetPasswordViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string email, string password, string? returnUrl = null, CancellationToken cancellationToken = default)
    {
        ViewData["ReturnUrl"] = returnUrl;

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            ModelState.AddModelError(string.Empty, "Vui lòng nhập đầy đủ email và mật khẩu.");
            return View();
        }

        var reader = await registrationService.AuthenticateReaderAsync(email, password, cancellationToken);
        if (reader == null)
        {
            await auditLogService.WriteAsync(
                email.Trim(),
                AuditActions.LoginFailed,
                $"Đăng nhập bạn đọc bằng email {email.Trim()} – sai email hoặc mật khẩu",
                AuditLogService.ClientIp(HttpContext),
                cancellationToken);
            ModelState.AddModelError(string.Empty, "Email hoặc mật khẩu không chính xác.");
            return View();
        }

        // Chỉ báo khi mật khẩu đã đúng, nên không làm lộ việc email có tồn tại hay không.
        if (reader.IsLocked)
        {
            await LogReaderActivityAsync(reader.Id, AuditActions.LoginFailed, "tài khoản đang bị khoá", cancellationToken);
            ModelState.AddModelError(string.Empty,
                "Tài khoản của bạn đang bị khoá. Vui lòng liên hệ thư viện để được hỗ trợ.");
            return View();
        }
        if (!reader.EmailConfirmed)
        {
            await LogReaderActivityAsync(reader.Id, AuditActions.LoginFailed, "email chưa được xác nhận", cancellationToken);
            ModelState.AddModelError(string.Empty,
                "Email chưa được xác nhận. Vui lòng mở liên kết xác nhận trong email chúng tôi đã gửi khi bạn đăng ký.");
            ViewBag.UnconfirmedEmail = reader.Email;
            return View();
        }

        SetReaderSessionCookies(reader);
        await auditLogService.WriteAsync(
            reader.Email,
            AuditActions.Login,
            $"Tài khoản bạn đọc #{reader.Id} ({reader.Email})",
            AuditLogService.ClientIp(HttpContext),
            cancellationToken);

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction(nameof(Profile), new { id = reader.Id });
    }

    [HttpGet]
    public async Task<IActionResult> ConfirmEmail(string? token, CancellationToken cancellationToken = default)
    {
        var outcome = await emailVerificationService.ConfirmAsync(token ?? string.Empty, cancellationToken);
        if (outcome.IssuedCard is { } card)
        {
            var reader = await registrationService.GetReaderByIdAsync(card.ReaderAccountId, cancellationToken);
            await auditLogService.WriteAsync(
                reader?.Email ?? "Hệ thống",
                AuditActions.IssueCard,
                $"Thẻ {card.CardCode} – bạn đọc #{card.ReaderAccountId} (tự động khi xác nhận email)",
                AuditLogService.ClientIp(HttpContext),
                cancellationToken);
        }
        if (outcome.Result == EmailConfirmationResult.EmailChanged)
        {
            var reader = await registrationService.GetReaderByIdAsync(outcome.ReaderAccountId!.Value, cancellationToken);
            await auditLogService.WriteAsync(
                reader?.Email ?? "Hệ thống",
                AuditActions.UpdateAccount,
                $"Tài khoản bạn đọc #{outcome.ReaderAccountId} (đổi email {outcome.PreviousEmail} → {reader?.Email}, đã xác nhận)",
                AuditLogService.ClientIp(HttpContext),
                cancellationToken);
            ViewBag.NewEmail = reader?.Email;
        }

        ViewBag.Result = outcome.Result;
        ViewBag.IssuedCard = outcome.IssuedCard;
        return View();
    }

    [HttpGet]
    public IActionResult ResendConfirmation(string? email = null) =>
        View(new ResendEmailConfirmationViewModel { Email = email ?? string.Empty });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResendConfirmation(ResendEmailConfirmationViewModel model, CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid) return View(model);

        var result = await emailVerificationService.ResendAsync(model.Email, ConfirmEmailUrl(), cancellationToken);
        // Cùng một thông báo cho mọi trường hợp để không lộ email nào đã đăng ký.
        ViewBag.Message = "Nếu email đã đăng ký và chưa được xác nhận, chúng tôi đã gửi lại liên kết xác nhận (hiệu lực 24 giờ). Vui lòng kiểm tra hộp thư, kể cả mục Thư rác.";
        if (result is { Sent: false } && IsDevelopment()) ViewBag.DevConfirmLink = result.Link;
        return View(new ResendEmailConfirmationViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken = default)
    {
        var readerId = await GetCurrentLoggedInReaderIdAsync(cancellationToken);
        if (readerId > 0) await LogReaderActivityAsync(readerId, AuditActions.Logout, "đăng xuất", cancellationToken);
        ReaderSessionCookies.Clear(Response);
        return RedirectToAction(nameof(Login));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> HoldDocument(
        int documentId,
        int? readerId = null,
        CancellationToken cancellationToken = default)
    {
        int targetId = await GetCurrentLoggedInReaderIdAsync(cancellationToken);
        if (targetId <= 0)
        {
            return RedirectToAction(nameof(Login));
        }
        if (readerId.HasValue && readerId.Value != targetId)
        {
            TempData["HoldErrorMessage"] = "Bạn chỉ có thể đặt giữ tài liệu cho chính tài khoản của mình.";
            return RedirectToAction(nameof(Profile));
        }

        var outcome = await registrationService.HoldDocumentAsync(targetId, documentId, cancellationToken);
        await LogHoldOutcomeAsync(targetId, documentId, outcome, cancellationToken);
        if (!outcome.IsAllowed)
        {
            TempData["HoldErrorMessage"] = outcome.Message;
        }
        else
        {
            TempData["HoldSuccessMessage"] = outcome.Message;
        }

        return RedirectToAction(nameof(Profile), new { id = targetId });
    }

    [HttpPost("api/documents/{documentId}/hold")]
    public async Task<IActionResult> HoldDocumentApi(
        int documentId,
        [FromQuery] int? readerId = null,
        CancellationToken cancellationToken = default)
    {
        int targetId = await GetCurrentLoggedInReaderIdAsync(cancellationToken);
        if (targetId <= 0)
        {
            return Unauthorized(new { message = "Vui lòng đăng nhập trước khi thực hiện đặt giữ tài liệu." });
        }
        if (readerId.HasValue && readerId.Value != targetId)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                isAllowed = false,
                status = "Rejected",
                message = "Bạn chỉ có thể đặt giữ tài liệu cho chính tài khoản của mình."
            });
        }

        var outcome = await registrationService.HoldDocumentAsync(targetId, documentId, cancellationToken);
        await LogHoldOutcomeAsync(targetId, documentId, outcome, cancellationToken);
        if (!outcome.IsAllowed)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                isAllowed = false,
                status = "Rejected",
                message = outcome.Message
            });
        }

        return Ok(new
        {
            isAllowed = true,
            status = "Success",
            message = outcome.Message,
            requestStatus = outcome.IsReserved ? BookHoldStatus.Available : BookHoldStatus.Waiting,
            copyBarcode = outcome.CopyCode,
            pickupDeadlineUtc = outcome.PickupDeadlineUtc,
            queuePosition = outcome.QueuePosition
        });
    }

    // S2-08: Bạn đọc chỉ xem/hủy đơn của chính mình; mã bạn đọc luôn lấy từ phiên đăng nhập, không nhận từ client.
    [HttpGet]
    public async Task<IActionResult> MyHolds(CancellationToken cancellationToken = default)
    {
        var readerId = await GetCurrentLoggedInReaderIdAsync(cancellationToken);
        if (readerId <= 0)
        {
            return RedirectToAction(nameof(Login), new { returnUrl = Url.Action(nameof(MyHolds)) });
        }

        return View(await registrationService.GetReaderHoldsAsync(readerId, cancellationToken));
    }

    [HttpGet("api/reader/holds")]
    public async Task<IActionResult> GetMyHoldsApi(CancellationToken cancellationToken = default)
    {
        var readerId = await GetCurrentLoggedInReaderIdAsync(cancellationToken);
        if (readerId <= 0)
            return Unauthorized(new { message = "Vui lòng đăng nhập để xem đơn đặt giữ." });

        var holds = await registrationService.GetReaderHoldsAsync(readerId, cancellationToken);
        return Ok(holds.Select(hold => new
        {
            id = hold.Id,
            bookId = hold.BookId,
            bookTitle = hold.BookTitle,
            heldAtUtc = hold.HeldAtUtc,
            heldAt = HoldPickupExpirySchedule.GetVietnamLocalTime(hold.HeldAtUtc).ToString("dd/MM/yyyy HH:mm"),
            status = hold.Status,
            queuePosition = hold.QueuePosition,
            pickupDeadlineUtc = hold.PickupDeadlineUtc,
            pickupDeadline = hold.PickupDeadlineText,
            copyBarcode = hold.CopyBarcode,
            canCancel = hold.CanCancel
        }));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmPickup(long holdId, CancellationToken cancellationToken = default)
    {
        var readerId = await GetCurrentLoggedInReaderIdAsync(cancellationToken);
        if (readerId <= 0)
            return RedirectToAction(nameof(Login), new { returnUrl = Url.Action(nameof(MyHolds)) });

        var ownedHold = (await registrationService.GetReaderHoldsAsync(readerId, cancellationToken))
            .FirstOrDefault(hold => hold.Id == holdId);
        if (ownedHold == null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy đơn đặt giữ của tài khoản này.";
            return RedirectToAction(nameof(MyHolds));
        }

        if (!BookHoldStatus.WaitingPickupStatuses.Contains(ownedHold.Status))
        {
            TempData["ErrorMessage"] = $"Đơn đặt giữ hiện ở trạng thái ‘{ownedHold.Status}’, không thể xác nhận nhận sách.";
            return RedirectToAction(nameof(MyHolds));
        }

        var reader = await registrationService.GetReaderByIdAsync(readerId, cancellationToken);
        var cardCode = reader?.LibraryCard?.CardCode;
        if (string.IsNullOrWhiteSpace(cardCode))
        {
            TempData["ErrorMessage"] = "Tài khoản chưa có mã thẻ thư viện hợp lệ.";
            return RedirectToAction(nameof(MyHolds));
        }

        if (holdPickupConfirmationService is null)
        {
            TempData["ErrorMessage"] = "Dịch vụ xác nhận nhận sách chưa được cấu hình.";
            return RedirectToAction(nameof(MyHolds));
        }

        var actor = $"Bạn đọc #{readerId}";
        var outcome = await holdPickupConfirmationService.ConfirmAsync(
            holdId, cardCode, actor, null, cancellationToken);
        TempData[outcome.IsSuccess ? "SuccessMessage" : "ErrorMessage"] = outcome.Message;
        await LogReaderActivityAsync(readerId, "ConfirmHoldPickup",
            outcome.IsSuccess
                ? $"xác nhận đã nhận sách tại quầy cho đơn #{holdId}; phiếu mượn #{outcome.LoanId}"
                : $"xác nhận nhận sách thất bại cho đơn #{holdId}: {outcome.Message}",
            cancellationToken);
        return RedirectToAction(nameof(MyHolds));
    }

    [HttpPost("api/reader/holds/{holdId:long}/cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelMyHoldApi(long holdId, CancellationToken cancellationToken = default)
    {
        var readerId = await GetCurrentLoggedInReaderIdAsync(cancellationToken);
        if (readerId <= 0)
            return Unauthorized(new { message = "Vui lòng đăng nhập để hủy đơn đặt giữ." });

        var holdBefore = (await registrationService.GetReaderHoldsAsync(readerId, cancellationToken))
            .FirstOrDefault(hold => hold.Id == holdId);
        var outcome = await registrationService.CancelReaderHoldAsync(readerId, holdId, cancellationToken);
        if (holdBefore is not null)
        {
            var book = $"\"{holdBefore.BookTitle}\" (sách #{holdBefore.BookId})";
            await LogReaderActivityAsync(readerId, AuditActions.CancelHold,
                outcome.Result == BookHoldCancelResult.Success
                    ? $"bạn đọc tự hủy đơn đặt giữ #{holdId} {book}"
                    : $"hủy đơn đặt giữ #{holdId} {book} thất bại: {outcome.Message}",
                cancellationToken);
        }
        return outcome.Result switch
        {
            BookHoldCancelResult.Success => Ok(new
            {
                id = holdId,
                status = outcome.Status,
                message = outcome.Message,
                promotedHoldId = outcome.PromotedHoldId,
                releasedCopyId = outcome.ReleasedCopyId
            }),
            BookHoldCancelResult.NotWaiting => Conflict(new { id = holdId, status = outcome.Status, message = outcome.Message }),
            _ => NotFound(new { message = outcome.Message })
        };
    }

    private async Task<ViewResult> RegisterViewAsync(ReaderRegistrationViewModel model, CancellationToken cancellationToken)
    {
        model.CardTypes = await registrationService.GetActiveCardTypesAsync(cancellationToken);
        return View("Register", model);
    }

    private string ConfirmEmailUrl() =>
        Url.Action(nameof(ConfirmEmail), "ReaderRegistration", null, Request.Scheme)!;

    private bool IsDevelopment() =>
        HttpContext.RequestServices?.GetService<IWebHostEnvironment>()?.IsDevelopment() == true;

    private void ReportVerificationEmail(string email, EmailVerificationSendResult result)
    {
        TempData["VerificationEmail"] = email;
        TempData["VerificationEmailSent"] = result.Sent;
        // Chưa cấu hình SMTP: chỉ ở môi trường phát triển mới hiện liên kết để thử.
        if (!result.Sent && IsDevelopment()) TempData["DevConfirmLink"] = result.Link;
    }

    /// <summary>Ghi nhật ký một hoạt động của bạn đọc: người thực hiện là email bạn đọc, đối tượng ghi rõ mã tài khoản.</summary>
    private async Task LogReaderActivityAsync(int readerId, string action, string detail, CancellationToken cancellationToken)
    {
        var reader = await registrationService.GetReaderByIdAsync(readerId, cancellationToken);
        var email = reader?.Email ?? $"Bạn đọc #{readerId}";
        await auditLogService.WriteAsync(
            email,
            action,
            $"Tài khoản bạn đọc #{readerId} ({email}) – {detail}",
            AuditLogService.ClientIp(HttpContext),
            cancellationToken);
    }

    private async Task LogHoldOutcomeAsync(int readerId, int documentId, DocumentHoldOutcome outcome, CancellationToken cancellationToken)
    {
        var book = await BookTitleForHoldAsync(readerId, documentId, cancellationToken);
        var detail = !outcome.IsAllowed
            ? $"đặt giữ {book} bị từ chối: {outcome.Message}"
            : outcome.IsReserved
                ? $"đặt giữ {book} – đã có sách, giữ bản sao {outcome.CopyCode}"
                : $"đặt giữ {book} – xếp hàng chờ" + (outcome.QueuePosition is { } position ? $", vị trí {position}" : string.Empty);
        await LogReaderActivityAsync(readerId, AuditActions.PlaceHold, detail, cancellationToken);
    }

    private async Task<string> BookTitleForHoldAsync(int readerId, int documentId, CancellationToken cancellationToken)
    {
        var holds = await registrationService.GetReaderHoldsAsync(readerId, cancellationToken);
        var title = holds.FirstOrDefault(hold => hold.BookId == documentId)?.BookTitle;
        return title is null ? $"sách #{documentId}" : $"\"{title}\" (sách #{documentId})";
    }

    private void SetReaderSessionCookies(ReaderAccount reader) =>
        ReaderSessionCookies.Append(HttpContext, dataProtectionProvider, reader);

    private Task<int> GetCurrentLoggedInReaderIdAsync(CancellationToken cancellationToken) =>
        ReaderSessionCookies.GetReaderIdAsync(HttpContext, dataProtectionProvider, registrationService.GetReaderByIdAsync, cancellationToken);
}
