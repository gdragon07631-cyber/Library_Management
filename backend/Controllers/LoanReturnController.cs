using Microsoft.AspNetCore.Mvc;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

[StaffOnly(AccountRoles.Librarian, AccountRoles.LibraryManager, AccountRoles.SystemAdmin)]
public sealed class LoanReturnController(IBookLoanService loans) : Controller
{
    [HttpGet]
    public IActionResult Index() => View();

    [HttpGet("api/loan-returns/lookup")]
    public async Task<IActionResult> Lookup([FromQuery] string? barcode, CancellationToken ct = default)
    {
        var code = barcode?.Trim();
        if (string.IsNullOrWhiteSpace(code))
            return BadRequest(new { status = "invalidBarcode", message = "Vui lòng nhập mã vạch bản sao." });
        if (code.Length > 50)
            return BadRequest(new { status = "invalidBarcode", message = "Mã vạch không được vượt quá 50 ký tự." });

        try
        {
            var outcome = await loans.LookupByCopyCodeAsync(code, DateOnly.FromDateTime(DateTime.Today), ct);
            return outcome.Status switch
            {
                BookCopyLoanLookupStatus.CopyNotFound => NotFound(new
                {
                    status = "notFound", message = "Không tìm thấy bản sao có mã vạch này."
                }),
                BookCopyLoanLookupStatus.NotBorrowed => Ok(new
                {
                    status = "notBorrowed", message = "Bản sao này hiện không có ai mượn.", outcome.CopyCode
                }),
                BookCopyLoanLookupStatus.Borrowed => Ok(new
                {
                    status = "borrowed", outcome.LoanId, outcome.CopyCode, outcome.ReaderName,
                    outcome.BookTitle, outcome.DueDate, outcome.OverdueDays
                }),
                BookCopyLoanLookupStatus.UnassignedOpenLoan => Conflict(new
                {
                    status = "unassignedOpenLoan",
                    message = "Phiếu mượn của đầu sách chưa gắn mã bản sao nên chưa xác định được người mượn theo mã vạch."
                }),
                _ => Conflict(new
                {
                    status = "inconsistent", message = "Dữ liệu mượn trả của bản sao chưa nhất quán."
                })
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { status = "error", message = "Không thể tra cứu lúc này. Vui lòng thử lại." });
        }
    }

    [HttpPost("api/loan-returns/{id:long}/return"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmReturn(long id, CancellationToken ct = default)
    {
        try
        {
            var outcome = await loans.ReturnAsync(id, DateTime.UtcNow, ct);
            if (!outcome.IsSuccess)
            {
                var status = outcome.IsNotFound ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict;
                return StatusCode(status, new { success = false, message = outcome.ErrorMessage });
            }

            return Ok(new
            {
                success = true,
                message = outcome.AssignedHold is null
                    ? "Nhận trả sách thành công. Bản sao đã chuyển sang trạng thái Sẵn sàng."
                    : "Nhận trả sách thành công. Bản sao đã được giữ cho người đầu hàng đợi.",
                copyCode = outcome.Loan!.BookCopy?.CopyCode,
                readerName = outcome.Loan.ReaderAccount?.FullName,
                returnedAtUtc = outcome.Loan.ReturnedAtUtc,
                loanStatus = "Đã trả",
                copyStatus = outcome.CopyStatus,
                hasWaitingHold = outcome.AssignedHold is not null,
                reservedFor = outcome.AssignedHold is null ? null : new
                {
                    readerName = outcome.AssignedHold.ReaderAccount?.FullName,
                    readerCode = outcome.AssignedHold.ReaderAccount?.StudentOrStaffCode
                }
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { success = false, message = "Không thể xác nhận trả lúc này. Vui lòng thử lại." });
        }
    }
}
