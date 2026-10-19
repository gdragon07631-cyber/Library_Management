using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

[StaffOnly(AccountRoles.Librarian, AccountRoles.LibraryManager, AccountRoles.SystemAdmin)]
public sealed class LoanReturnController(
    IBookLoanService loans,
    ApplicationDbContext? db = null,
    IWorkingScheduleService? workingScheduleService = null) : Controller
{
    [HttpGet]
    public IActionResult Index() => View();

    [HttpGet("api/loan-returns/search-readers")]
    public async Task<IActionResult> SearchReaders([FromQuery] string? query, [FromQuery] DateOnly? returnDate, CancellationToken ct = default)
    {
        if (db is null)
            return StatusCode(StatusCodes.Status500InternalServerError, new { status = "error", message = "Không thể kết nối cơ sở dữ liệu." });

        var term = query?.Trim();
        if (string.IsNullOrWhiteSpace(term))
            return BadRequest(new { status = "invalidQuery", message = "Vui lòng nhập tên bạn đọc, mã bạn đọc, mã thẻ hoặc email." });
        if (term.Length > 150)
            return BadRequest(new { status = "invalidQuery", message = "Từ khóa tìm kiếm không được vượt quá 150 ký tự." });

        try
        {
            var effectiveReturnDate = returnDate ?? LibraryCalendarDate.Today();
            var matchedReaders = await db.ReaderAccounts.AsNoTracking()
                .Include(r => r.LibraryCard)
                .Where(r => r.FullName.Contains(term)
                    || r.StudentOrStaffCode.Contains(term)
                    || r.Email.Contains(term)
                    || r.PhoneNumber.Contains(term)
                    || (r.LibraryCard != null && r.LibraryCard.CardCode.Contains(term)))
                .OrderBy(r => r.FullName)
                .ThenBy(r => r.Id)
                .Take(20)
                .ToListAsync(ct);

            if (matchedReaders.Count == 0)
            {
                return Ok(new
                {
                    status = "notFound",
                    message = $"Không tìm thấy bạn đọc nào khớp với \"{term}\".",
                    readers = Array.Empty<object>()
                });
            }

            var readerIds = matchedReaders.Select(r => r.Id).ToList();
            var readerLoans = await db.BookLoans.AsNoTracking()
                .Include(l => l.Book)
                .Include(l => l.BookCopy)
                .Where(l => readerIds.Contains(l.ReaderAccountId))
                .OrderBy(l => l.ReturnedAtUtc != null || l.CloseReason != null)
                .ThenBy(l => l.DueDate)
                .ThenByDescending(l => l.LoanDate)
                .ThenByDescending(l => l.Id)
                .ToListAsync(ct);

            var dailyRate = (await db.LoanPolicies.AsNoTracking()
                .Where(p => p.Id == LoanPolicy.SingletonId)
                .Select(p => p.DailyOverdueFee)
                .SingleOrDefaultAsync(ct)) ?? BookLoan.DailyOverdueFeeRate;

            var loansByReader = readerLoans.ToLookup(l => l.ReaderAccountId);
            var readersPayload = new List<object>(matchedReaders.Count);

            foreach (var reader in matchedReaders)
            {
                var items = new List<object>();
                var list = loansByReader[reader.Id].ToList();
                var openCount = 0;

                foreach (var loan in list)
                {
                    var isOpen = loan.ReturnedAtUtc is null && string.IsNullOrEmpty(loan.CloseReason);
                    if (isOpen) openCount++;

                    var overdueDays = loan.OverdueDaysAtReturn;
                    var estimatedFee = loan.OverdueFee;

                    if (isOpen)
                    {
                        if (effectiveReturnDate > loan.DueDate)
                        {
                            try
                            {
                                if (workingScheduleService is not null)
                                {
                                    var openDates = await workingScheduleService.GetConfiguredOpenDatesAsync(
                                        loan.DueDate.AddDays(1), effectiveReturnDate, ct);
                                    var calc = OverdueFeeCalculator.Calculate(loan.DueDate, effectiveReturnDate, dailyRate, openDates);
                                    overdueDays = calc.OverdueDays;
                                    estimatedFee = calc.EstimatedFee;
                                }
                                else
                                {
                                    overdueDays = Math.Max(0, effectiveReturnDate.DayNumber - loan.DueDate.DayNumber);
                                    estimatedFee = overdueDays * dailyRate;
                                }
                            }
                            catch
                            {
                                overdueDays = Math.Max(0, effectiveReturnDate.DayNumber - loan.DueDate.DayNumber);
                                estimatedFee = overdueDays * dailyRate;
                            }
                        }
                        else
                        {
                            overdueDays = 0;
                            estimatedFee = 0m;
                        }
                    }

                    items.Add(new
                    {
                        loanId = loan.Id,
                        bookTitle = loan.Book?.Title ?? $"Sách #{loan.BookId}",
                        copyCode = loan.BookCopy?.CopyCode,
                        loanDate = loan.LoanDate,
                        dueDate = loan.DueDate,
                        returnDate = effectiveReturnDate,
                        returnedAtUtc = loan.ReturnedAtUtc,
                        closeReason = loan.CloseReason,
                        isOpen,
                        overdueDays,
                        dailyOverdueFee = isOpen ? dailyRate : (loan.DailyOverdueFeeAtReturn > 0 ? loan.DailyOverdueFeeAtReturn : dailyRate),
                        estimatedOverdueFee = estimatedFee,
                        readerName = reader.FullName
                    });
                }

                readersPayload.Add(new
                {
                    readerId = reader.Id,
                    readerName = reader.FullName,
                    readerCode = reader.StudentOrStaffCode,
                    email = reader.Email,
                    cardCode = reader.LibraryCard?.CardCode,
                    cardStatus = reader.LibraryCard?.Status ?? "Chưa có thẻ",
                    openLoanCount = openCount,
                    totalLoanCount = list.Count,
                    loans = items
                });
            }

            return Ok(new
            {
                status = "ok",
                returnDate = effectiveReturnDate,
                readers = readersPayload
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { status = "error", message = "Không thể tìm kiếm bạn đọc lúc này. Vui lòng thử lại." });
        }
    }

    [HttpGet("api/loan-returns/lookup")]
    public async Task<IActionResult> Lookup([FromQuery] string? barcode, [FromQuery] DateOnly? returnDate, CancellationToken ct = default)
    {
        var code = barcode?.Trim();
        if (string.IsNullOrWhiteSpace(code))
            return BadRequest(new { status = "invalidBarcode", message = "Vui lòng nhập mã vạch bản sao." });
        if (code.Length > 50)
            return BadRequest(new { status = "invalidBarcode", message = "Mã vạch không được vượt quá 50 ký tự." });

        try
        {
            var effectiveReturnDate = returnDate ?? LibraryCalendarDate.Today();
            var outcome = await loans.LookupByCopyCodeAsync(code, effectiveReturnDate, ct);
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
                    outcome.BookTitle, outcome.DueDate, outcome.OverdueDays,
                    returnDate = effectiveReturnDate, outcome.DailyOverdueFee, outcome.EstimatedOverdueFee
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
        catch (InvalidOperationException exception)
        {
            return Conflict(new { status = "scheduleNotConfigured", message = exception.Message });
        }
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
            var returnedAtUtc = DateTime.UtcNow;
            var outcome = await loans.ReturnAsync(id, returnedAtUtc, ct);
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
                dueDate = outcome.Loan.DueDate,
                returnDate = LibraryCalendarDate.DateFromUtc(returnedAtUtc),
                overdueDays = outcome.OverdueDays,
                overdueFee = outcome.Loan.OverdueFee,
                dailyOverdueFee = outcome.Loan.DailyOverdueFeeAtReturn,
                receivable = outcome.Receivable is null ? null : new
                {
                    id = outcome.Receivable.Id,
                    amount = outcome.Receivable.Amount,
                    status = outcome.Receivable.Status,
                    description = outcome.Receivable.Description
                },
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
