using System.ComponentModel.DataAnnotations;
using Project.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

[StaffOnly(AccountRoles.SystemAdmin, AccountRoles.LibraryManager)]
public sealed class LoanController(IBookLoanService loans, ApplicationDbContext db) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        if (!await IsLibrarianSignedInAsync(ct)) return RedirectToAction("Login", "Account", new { returnUrl = Url.Action(nameof(Index)) });
        return View(await BuildModel(ct));
    }

    public static bool HasOverridePermission(string? role) =>
        role is AccountRoles.LibraryManager or AccountRoles.SystemAdmin;

    private async Task<AdminAccount?> GetSignedInStaffAsync(CancellationToken ct)
    {
        if (Request == null) return null;
        var audit = HttpContext?.RequestServices?.GetService<IAuditLogService>();
        if (audit != null)
        {
            var staffFromService = await audit.GetSignedInStaffAsync(Request, ct);
            if (staffFromService != null) return staffFromService;
        }
        if (Request.Cookies == null || !Request.Cookies.TryGetValue("admin_refresh", out var token) || string.IsNullOrWhiteSpace(token))
            return null;
        var hash = TokenService.HashRefreshToken(token);
        var now = DateTime.UtcNow;
        return await db.RefreshTokens.AsNoTracking()
            .Where(item => item.TokenHash == hash && item.RevokedAtUtc == null && item.ExpiresAtUtc > now && item.AdminAccount.IsActive)
            .Select(item => item.AdminAccount)
            .SingleOrDefaultAsync(ct);
    }

    private async Task<bool> IsLibrarianSignedInAsync(CancellationToken ct)
    {
        if (!Request.Cookies.TryGetValue("admin_refresh", out var token) || string.IsNullOrWhiteSpace(token)) return false;
        var hash = TokenService.HashRefreshToken(token);
        return await db.RefreshTokens.AnyAsync(item => item.TokenHash == hash && item.RevokedAtUtc == null && item.ExpiresAtUtc > DateTime.UtcNow && item.AdminAccount.IsActive && (item.AdminAccount.Role == AccountRoles.SystemAdmin || item.AdminAccount.Role == AccountRoles.LibraryManager), ct);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateBookLoanViewModel model, CancellationToken ct = default)
    {
        if (!ModelState.IsValid || model.LoanDate == null)
        {
            TempData["ErrorMessage"] = ModelState.Values.SelectMany(value => value.Errors).FirstOrDefault()?.ErrorMessage ?? "Dữ liệu phiếu mượn không hợp lệ.";
            return View(nameof(Index), await BuildModel(ct));
        }

        var bookIds = model.BookIds != null && model.BookIds.Count > 0
            ? model.BookIds
            : (model.BookId > 0 ? [model.BookId] : new List<int>());

        if (bookIds.Count == 0)
        {
            TempData["ErrorMessage"] = "Vui lòng chọn sách.";
            return View(nameof(Index), await BuildModel(ct));
        }

        var staff = await GetSignedInStaffAsync(ct);
        var staffEmail = staff?.Email ?? "Thủ thư";

        if (bookIds.Count == 1)
        {
            var singleBookId = bookIds[0];
            var result = await loans.CreateAsync(singleBookId, model.ReaderAccountId, model.LoanDate.Value, staffEmail, ct);
            if (result.IsSuccess)
            {
                await LogLoanAsync(AuditActions.CreateLoan, singleBookId, model.ReaderAccountId,
                    $"lập phiếu mượn #{result.Loan!.Id}, ngày mượn {model.LoanDate:dd/MM/yyyy}, hạn trả {result.Loan!.DueDate:dd/MM/yyyy}", ct);
                TempData["SuccessMessage"] = $"Đã tạo phiếu mượn. Hạn trả: {result.Loan!.DueDate:dd/MM/yyyy}.";
            }
            else
            {
                TempData["ErrorMessage"] = result.ErrorMessage;
                TempData["BlockedErrorMessage"] = result.ErrorMessage;
                TempData["BlockedReaderId"] = model.ReaderAccountId;
                TempData["BlockedBookId"] = singleBookId;
                TempData["BlockedLoanDate"] = model.LoanDate.Value.ToString("yyyy-MM-dd");
            }
            return RedirectToAction(nameof(Index));
        }
        else
        {
            var batchResult = await loans.CreateManyAsync(bookIds, model.ReaderAccountId, model.LoanDate.Value, staffEmail, ct);
            if (batchResult.IsSuccess)
            {
                foreach (var loan in batchResult.Loans)
                {
                    await LogLoanAsync(AuditActions.CreateLoan, loan.BookId, model.ReaderAccountId,
                        $"lập phiếu mượn #{loan.Id}, ngày mượn {model.LoanDate:dd/MM/yyyy}, hạn trả {loan.DueDate:dd/MM/yyyy}", ct);
                }
                TempData["SuccessMessage"] = $"Đã tạo {batchResult.Loans.Count} phiếu mượn thành công.";
            }
            else
            {
                TempData["ErrorMessage"] = batchResult.ErrorMessage;
                TempData["BlockedErrorMessage"] = batchResult.ErrorMessage;
                TempData["BlockedReaderId"] = model.ReaderAccountId;
                TempData["BlockedBookIds"] = string.Join(",", bookIds);
                TempData["BlockedLoanDate"] = model.LoanDate.Value.ToString("yyyy-MM-dd");
            }
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Override(OverrideBookLoanViewModel model, CancellationToken ct = default)
    {
        var staff = await GetSignedInStaffAsync(ct);
        if (staff == null || !HasOverridePermission(staff.Role))
        {
            TempData["ErrorMessage"] = "Bạn không có quyền bỏ qua chặn cho mượn.";
            return RedirectToAction(nameof(Index));
        }

        if (string.IsNullOrWhiteSpace(model.BypassReason))
        {
            TempData["ErrorMessage"] = "Vui lòng nhập lý do bỏ qua.";
            return RedirectToAction(nameof(Index));
        }

        var bookIds = model.BookIds != null && model.BookIds.Count > 0
            ? model.BookIds
            : (model.BookId > 0 ? [model.BookId] : new List<int>());

        if (bookIds.Count == 0 || model.LoanDate == null)
        {
            TempData["ErrorMessage"] = "Dữ liệu phiếu mượn không hợp lệ.";
            return RedirectToAction(nameof(Index));
        }

        var result = await loans.OverrideCreateManyAsync(bookIds, model.ReaderAccountId, model.LoanDate.Value, staff.Email, model.BypassReason, ct);
        if (result.IsSuccess)
        {
            foreach (var loan in result.Loans)
            {
                await LogLoanAsync(AuditActions.CreateLoan, loan.BookId, model.ReaderAccountId,
                    $"lập phiếu mượn #{loan.Id}, ngày mượn {model.LoanDate:dd/MM/yyyy}, hạn trả {loan.DueDate:dd/MM/yyyy} (bỏ qua chặn)", ct);
            }
            TempData["SuccessMessage"] = $"Đã bỏ qua chặn và tạo {result.Loans.Count} phiếu mượn thành công.";
        }
        else
        {
            TempData["ErrorMessage"] = result.ErrorMessage;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Renew(long id, CancellationToken ct = default)
    {
        var result = await loans.RenewAsync(id, DateOnly.FromDateTime(DateTime.Today), ct);
        await LogRenewAsync(id, result, ct);
        TempData[result.IsSuccess ? "SuccessMessage" : "ErrorMessage"] = result.IsSuccess
            ? $"Đã gia hạn phiếu mượn. Hạn trả mới: {result.Loan!.DueDate:dd/MM/yyyy}. Số lần gia hạn: {result.Loan.RenewalCount} / {result.Loan.ReaderAccount!.LibraryCard!.LibraryCardType!.MaxRenewals}."
            : result.ErrorMessage;
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("api/loans/lookup")]
    public async Task<IActionResult> LookupByBarcodeApi([FromQuery] string? barcode, CancellationToken ct = default)
    {
        if (!await IsLibrarianSignedInAsync(ct)) return Unauthorized(new { message = "Vui lòng đăng nhập để tra cứu." });
        var normalizedBarcode = barcode?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedBarcode))
            return BadRequest(new { status = "invalidBarcode", message = "Vui lòng nhập mã vạch bản sao." });
        if (normalizedBarcode.Length > 50)
            return BadRequest(new { status = "invalidBarcode", message = "Mã vạch không được vượt quá 50 ký tự." });

        BookCopyLoanLookupOutcome result;
        try
        {
            result = await loans.LookupByCopyCodeAsync(normalizedBarcode, DateOnly.FromDateTime(DateTime.Today), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { status = "error", message = "Không thể tra cứu lúc này. Vui lòng thử lại." });
        }

        return result.Status switch
        {
            BookCopyLoanLookupStatus.CopyNotFound => NotFound(new
            {
                status = "notFound", message = "Không tìm thấy bản sao có mã vạch này."
            }),
            BookCopyLoanLookupStatus.NotBorrowed => Ok(new
            {
                status = "notBorrowed", message = "Bản sao này hiện không có ai mượn.", result.CopyCode
            }),
            BookCopyLoanLookupStatus.Borrowed => Ok(new
            {
                status = "borrowed", result.LoanId, result.CopyCode, result.ReaderName, result.BookTitle,
                result.DueDate, result.OverdueDays
            }),
            BookCopyLoanLookupStatus.UnassignedOpenLoan => Conflict(new
            {
                status = "unassignedOpenLoan",
                message = "Còn phiếu mượn cũ chưa gán bản sao cho sách này nên chưa thể xác định người mượn theo mã vạch."
            }),
            _ => Conflict(new
            {
                status = "inconsistent", message = "Dữ liệu phiếu mượn của bản sao này chưa nhất quán."
            })
        };
    }

    [HttpGet("api/loans")]
    public async Task<IActionResult> GetAllApi(CancellationToken ct = default) =>
        Ok((await loans.GetAllAsync(ct)).Select(ToApiModel));

    [HttpPost("api/loans")]
    public async Task<IActionResult> CreateApi([FromBody] CreateBookLoanViewModel? model, CancellationToken ct = default)
    {
        if (model?.LoanDate == null || !TryValidateModel(model))
            return BadRequest(new { message = "Dữ liệu phiếu mượn không hợp lệ." });


        var bookIds = model.BookIds != null && model.BookIds.Count > 0
            ? model.BookIds
            : (model.BookId > 0 ? [model.BookId] : new List<int>());

        if (bookIds.Count == 0)
            return BadRequest(new { message = "Dữ liệu phiếu mượn không hợp lệ." });

        var staff = await GetSignedInStaffAsync(ct);
        var staffEmail = staff?.Email ?? "Thủ thư";

        if (!string.IsNullOrWhiteSpace(model.BypassReason))
        {
            if (staff == null || !HasOverridePermission(staff.Role))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { message = "Bạn không có quyền bỏ qua chặn cho mượn." });
            }

            var overrideResult = await loans.OverrideCreateManyAsync(bookIds, model.ReaderAccountId, model.LoanDate.Value, staff.Email, model.BypassReason, ct);
            if (!overrideResult.IsSuccess) return BadRequest(new { message = overrideResult.ErrorMessage });
            foreach (var loan in overrideResult.Loans)
            {
                await LogLoanAsync(AuditActions.CreateLoan, loan.BookId, model.ReaderAccountId,
                    $"lập phiếu mượn #{loan.Id}, ngày mượn {model.LoanDate:dd/MM/yyyy}, hạn trả {loan.DueDate:dd/MM/yyyy} (bỏ qua chặn)", ct);
            }
            return bookIds.Count == 1
                ? Created($"/api/loans/{overrideResult.Loans[0].Id}", ToApiModel(overrideResult.Loans[0]))
                : Ok(overrideResult.Loans.Select(ToApiModel));
        }

        if (bookIds.Count == 1)
        {
            var singleBookId = bookIds[0];
            var result = await loans.CreateAsync(singleBookId, model.ReaderAccountId, model.LoanDate.Value, staffEmail, ct);
            if (!result.IsSuccess) return BadRequest(new { message = result.ErrorMessage });

            await LogLoanAsync(AuditActions.CreateLoan, singleBookId, model.ReaderAccountId,
                $"lập phiếu mượn #{result.Loan!.Id}, ngày mượn {model.LoanDate:dd/MM/yyyy}, hạn trả {result.Loan!.DueDate:dd/MM/yyyy}", ct);
            return Created($"/api/loans/{result.Loan!.Id}", ToApiModel(result.Loan));
        }
        else
        {
            var batchResult = await loans.CreateManyAsync(bookIds, model.ReaderAccountId, model.LoanDate.Value, staffEmail, ct);
            if (!batchResult.IsSuccess)
            {
                return BadRequest(new { message = batchResult.ErrorMessage });
            }
            foreach (var loan in batchResult.Loans)
            {
                await LogLoanAsync(AuditActions.CreateLoan, loan.BookId, model.ReaderAccountId,
                    $"lập phiếu mượn #{loan.Id}, ngày mượn {model.LoanDate:dd/MM/yyyy}, hạn trả {loan.DueDate:dd/MM/yyyy}", ct);
            }
            return Ok(batchResult.Loans.Select(ToApiModel));
        }
    }

    [HttpPost("api/loans/batch")]
    public async Task<IActionResult> CreateBatchApi([FromBody] CreateBatchBookLoanViewModel? model, CancellationToken ct = default)
    {
        if (model?.LoanDate == null || !ValidateModel(model) || model.BookIds.Count == 0)
            return BadRequest(new { message = "Dữ liệu phiếu mượn không hợp lệ." });

        var staff = await GetSignedInStaffAsync(ct);
        var staffEmail = staff?.Email ?? "Thủ thư";

        if (!string.IsNullOrWhiteSpace(model.BypassReason))
        {
            if (staff == null || !HasOverridePermission(staff.Role))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { message = "Bạn không có quyền bỏ qua chặn cho mượn." });
            }

            var overrideResult = await loans.OverrideCreateManyAsync(model.BookIds, model.ReaderAccountId, model.LoanDate.Value, staff.Email, model.BypassReason, ct);
            if (!overrideResult.IsSuccess) return BadRequest(new { message = overrideResult.ErrorMessage });
            foreach (var loan in overrideResult.Loans)
            {
                await LogLoanAsync(AuditActions.CreateLoan, loan.BookId, model.ReaderAccountId,
                    $"lập phiếu mượn #{loan.Id}, ngày mượn {model.LoanDate:dd/MM/yyyy}, hạn trả {loan.DueDate:dd/MM/yyyy} (bỏ qua chặn)", ct);
            }
            return Ok(overrideResult.Loans.Select(ToApiModel));
        }

        var batchResult = await loans.CreateManyAsync(model.BookIds, model.ReaderAccountId, model.LoanDate.Value, staffEmail, ct);
        if (!batchResult.IsSuccess)
        {
            return BadRequest(new { message = batchResult.ErrorMessage });
        }
        foreach (var loan in batchResult.Loans)
        {
            await LogLoanAsync(AuditActions.CreateLoan, loan.BookId, model.ReaderAccountId,
                $"lập phiếu mượn #{loan.Id}, ngày mượn {model.LoanDate:dd/MM/yyyy}, hạn trả {loan.DueDate:dd/MM/yyyy}", ct);
        }
        return Ok(batchResult.Loans.Select(ToApiModel));
    }

    [HttpPost("api/loans/override")]
    public async Task<IActionResult> OverrideApi([FromBody] OverrideBookLoanViewModel? model, CancellationToken ct = default)
    {
        var staff = await GetSignedInStaffAsync(ct);
        if (staff == null || !HasOverridePermission(staff.Role))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Bạn không có quyền bỏ qua chặn cho mượn." });
        }

        if (model?.LoanDate == null)
        {
            return BadRequest(new { message = "Dữ liệu phiếu mượn không hợp lệ." });
        }

        if (string.IsNullOrWhiteSpace(model.BypassReason))
        {
            return BadRequest(new { message = "Vui lòng nhập lý do bỏ qua." });
        }

        var bookIds = model.BookIds != null && model.BookIds.Count > 0
            ? model.BookIds
            : (model.BookId > 0 ? [model.BookId] : new List<int>());

        if (bookIds.Count == 0)
        {
            return BadRequest(new { message = "Dữ liệu phiếu mượn không hợp lệ." });
        }

        var result = await loans.OverrideCreateManyAsync(bookIds, model.ReaderAccountId, model.LoanDate.Value, staff.Email, model.BypassReason, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new { message = result.ErrorMessage });
        }

        foreach (var loan in result.Loans)
        {
            await LogLoanAsync(AuditActions.CreateLoan, loan.BookId, model.ReaderAccountId,
                $"lập phiếu mượn #{loan.Id}, ngày mượn {model.LoanDate:dd/MM/yyyy}, hạn trả {loan.DueDate:dd/MM/yyyy} (bỏ qua chặn)", ct);
        }

        return Ok(result.Loans.Select(ToApiModel));
    }

    private bool ValidateModel(object? model)
    {
        if (model == null) return false;
        try
        {
            if (ObjectValidator != null) return TryValidateModel(model);
        }
        catch (NullReferenceException) { }
        var context = new ValidationContext(model);
        var results = new List<ValidationResult>();
        return Validator.TryValidateObject(model, context, results, true);
    }

    [HttpPost("api/loans/{id:long}/return")]
    public async Task<IActionResult> ReturnApi(long id, CancellationToken ct = default)
    {
        if (!await IsLibrarianSignedInAsync(ct)) return Unauthorized(new { message = "Vui lòng đăng nhập để xác nhận trả sách." });
        try
        {
            var result = await loans.ReturnAsync(id, DateTime.UtcNow, ct);
            if (!result.IsSuccess)
            {
                var statusCode = result.IsNotFound ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict;
                return StatusCode(statusCode, new { success = false, message = result.ErrorMessage });
            }
            return Ok(new
            {
                success = true,
                message = result.AssignedHold is null
                    ? "Nhận trả sách thành công. Bản sao đã được chuyển sang trạng thái Sẵn sàng."
                    : "Nhận trả sách thành công. Bản sao đã được chuyển sang trạng thái Đang giữ cho người đặt giữ đầu hàng đợi.",
                copyCode = result.Loan!.BookCopy?.CopyCode,
                readerName = result.Loan.ReaderAccount?.FullName,
                returnedAtUtc = result.Loan.ReturnedAtUtc,
                loanStatus = "Đã trả",
                copyStatus = result.CopyStatus,
                hasWaitingHold = result.AssignedHold is not null,
                reservedFor = result.AssignedHold is null ? null : new
                {
                    readerName = result.AssignedHold.ReaderAccount?.FullName,
                    readerCode = result.AssignedHold.ReaderAccount?.StudentOrStaffCode
                }
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { success = false, message = "Không thể xác nhận trả lúc này. Vui lòng thử lại." });
        }
    }

    [HttpPost("api/loans/{id:long}/renew")]
    public async Task<IActionResult> RenewApi(long id, CancellationToken ct = default)
    {
        var result = await loans.RenewAsync(id, DateOnly.FromDateTime(DateTime.Today), ct);
        await LogRenewAsync(id, result, ct);
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

    private async Task<LoanIndexViewModel> BuildModel(CancellationToken ct)
    {
        var readers = await db.ReaderAccounts.AsNoTracking()
            .Include(reader => reader.LibraryCard)
                .ThenInclude(card => card!.LibraryCardType)
            .Where(reader => reader.Status == "Đang hoạt động")
            .OrderBy(reader => reader.FullName)
            .ToListAsync(ct);

        var readerIds = readers.Select(r => r.Id).ToList();
        var allLoans = await db.BookLoans.AsNoTracking()
            .Where(l => readerIds.Contains(l.ReaderAccountId))
            .ToListAsync(ct);

        var loanCounts = allLoans
            .Where(l => !l.IsReturned)
            .GroupBy(l => l.ReaderAccountId)
            .ToDictionary(g => g.Key, g => g.Count());

        var today = DateOnly.FromDateTime(DateTime.Today);
        var overdueReaderIds = allLoans
            .Where(l => !l.IsReturned && l.DueDate < today)
            .Select(l => l.ReaderAccountId)
            .ToHashSet();

        var staff = await GetSignedInStaffAsync(ct);
        var canOverride = staff != null && HasOverridePermission(staff.Role);

        return new()
        {
            Loans = await loans.GetAllAsync(ct),
            Books = await db.Books.AsNoTracking().OrderBy(book => book.Title).ToListAsync(ct),
            AvailableCopies = await db.BookCopies.AsNoTracking().Include(copy => copy.Book)
                .Where(copy => copy.Status == BookCopyStatus.Available &&
                    !db.BookLoans.Any(loan => loan.BookCopyId == copy.Id && loan.ReturnedAtUtc == null))
                .OrderBy(copy => copy.CopyCode).ToListAsync(ct),
            Readers = readers,
            ReaderLoanCounts = loanCounts,
            ReaderHasOverdue = overdueReaderIds,
            BlockedLoanLogs = await loans.GetBlockedLoanLogsAsync(null, ct),
            NewLoan = new CreateBookLoanViewModel { LoanDate = today },
            CanOverride = canOverride
        };
    }

    private async Task LogLoanAsync(string action, int bookId, int readerId, string detail, CancellationToken ct)
    {
        var audit = HttpContext?.RequestServices?.GetService<IAuditLogService>();
        if (audit is null) return;
        var staff = await audit.GetSignedInStaffAsync(Request, ct);
        var title = await db.Books.AsNoTracking().Where(book => book.Id == bookId).Select(book => book.Title).SingleOrDefaultAsync(ct);
        var reader = await db.ReaderAccounts.AsNoTracking().Where(item => item.Id == readerId)
            .Select(item => new { item.FullName, item.Email }).SingleOrDefaultAsync(ct);
        await audit.WriteAsync(
            staff?.Email ?? "Không xác định",
            action,
            $"Sách \"{title}\" (#{bookId}) – bạn đọc #{readerId} {reader?.FullName} ({reader?.Email}) – {detail}",
            AuditLogService.ClientIp(HttpContext!),
            ct);
    }

    private async Task LogRenewAsync(long loanId, RenewBookLoanOutcome result, CancellationToken ct)
    {
        var loan = await db.BookLoans.AsNoTracking().Where(item => item.Id == loanId)
            .Select(item => new { item.BookId, item.ReaderAccountId }).SingleOrDefaultAsync(ct);
        if (loan is null) return;
        await LogLoanAsync(AuditActions.RenewLoan, loan.BookId, loan.ReaderAccountId, result.IsSuccess
            ? $"gia hạn phiếu mượn #{loanId}: hạn trả {result.OldDueDate:dd/MM/yyyy} → {result.Loan!.DueDate:dd/MM/yyyy}, lần gia hạn thứ {result.Loan.RenewalCount}"
            : $"gia hạn phiếu mượn #{loanId} thất bại: {result.ErrorMessage}", ct);
    }

    private static object ToApiModel(BookLoan loan) => new
    {
        loan.Id, loan.BookId, bookTitle = loan.Book?.Title, loan.ReaderAccountId,
        readerName = loan.ReaderAccount?.FullName, loan.LoanDate, loan.OriginalDueDate, loan.DueDate,
        renewalCount = loan.RenewalCount,
        renewalLimit = loan.ReaderAccount?.LibraryCard?.LibraryCardType?.MaxRenewals ?? 0,
        wasDueDateAdjusted = loan.OriginalDueDate != loan.DueDate,
        canRenew = !loan.IsReturned && loan.DueDate >= DateOnly.FromDateTime(DateTime.Today)
    };
}
