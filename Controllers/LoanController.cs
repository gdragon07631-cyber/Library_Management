using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

public sealed class LoanController(IBookLoanService loans, ApplicationDbContext db, ILogger<LoanController> logger) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        if (!await IsLibrarianSignedInAsync(ct)) return RedirectToAction("Login", "Account", new { returnUrl = Url.Action(nameof(Index)) });
        return View(await BuildModel(ct));
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

        var result = await loans.CreateAsync(model.BookId, model.ReaderAccountId, model.LoanDate.Value, ct, model.BookCopyId);
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

    [HttpGet("api/loans")]
    public async Task<IActionResult> GetAllApi(CancellationToken ct = default) =>
        Ok((await loans.GetAllAsync(ct)).Select(ToApiModel));

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
        catch (Exception exception)
        {
            logger.LogError(exception, "Lỗi tra cứu phiếu mượn bằng mã vạch.");
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

    [HttpPost("api/loans")]
    public async Task<IActionResult> CreateApi([FromBody] CreateBookLoanViewModel? model, CancellationToken ct = default)
    {
        if (model?.LoanDate == null || !TryValidateModel(model))
            return BadRequest(new { message = "Dữ liệu phiếu mượn không hợp lệ." });
        var result = await loans.CreateAsync(model.BookId, model.ReaderAccountId, model.LoanDate.Value, ct, model.BookCopyId);
        if (!result.IsSuccess) return BadRequest(new { message = result.ErrorMessage });
        return Created($"/api/loans/{result.Loan!.Id}", ToApiModel(result.Loan));
    }

    [HttpPost("api/loans/{id:long}/return"), ValidateAntiForgeryToken]
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
        catch (Exception exception)
        {
            logger.LogError(exception, "Lỗi xác nhận trả phiếu mượn #{LoanId}.", id);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { success = false, message = "Không thể xác nhận trả lúc này. Vui lòng thử lại." });
        }
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
        AvailableCopies = await db.BookCopies.AsNoTracking().Include(copy => copy.Book)
            .Where(copy => copy.Status == BookCopyStatuses.Available &&
                !db.BookLoans.Any(loan => loan.BookCopyId == copy.Id && loan.ReturnedAtUtc == null))
            .OrderBy(copy => copy.CopyCode).ToListAsync(ct),
        Readers = await db.ReaderAccounts.AsNoTracking()
            .Where(reader => reader.Status == "Đang hoạt động").OrderBy(reader => reader.FullName).ToListAsync(ct),
        NewLoan = new CreateBookLoanViewModel { LoanDate = DateOnly.FromDateTime(DateTime.Today) }
    };

    private static object ToApiModel(BookLoan loan) => new
    {
        loan.Id, loan.BookId, bookTitle = loan.Book?.Title, loan.BookCopyId, copyCode = loan.BookCopy?.CopyCode,
        loan.ReturnedAtUtc, loan.ReaderAccountId,
        readerName = loan.ReaderAccount?.FullName, loan.LoanDate, loan.OriginalDueDate, loan.DueDate,
        renewalCount = loan.RenewalCount,
        renewalLimit = loan.ReaderAccount?.LibraryCard?.LibraryCardType?.MaxRenewals ?? 0,
        wasDueDateAdjusted = loan.OriginalDueDate != loan.DueDate,
        canRenew = !loan.ReturnedAtUtc.HasValue && loan.DueDate >= DateOnly.FromDateTime(DateTime.Today)
    };
}
