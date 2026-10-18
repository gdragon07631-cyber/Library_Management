using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

[StaffOnly(AccountRoles.Librarian, AccountRoles.LibraryManager)]
public sealed class ReaderLoanHistoryController(ApplicationDbContext db) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Details(
        int id, DateOnly? startDate = null, DateOnly? endDate = null, CancellationToken cancellationToken = default)
    {
        var reader = await db.ReaderAccounts.AsNoTracking()
            .Include(item => item.LibraryCard)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (reader is null) return NotFound("Không tìm thấy bạn đọc.");

        string? dateFilterError = null;
        if (!ModelState.IsValid)
            dateFilterError = "Ngày lọc không hợp lệ. Vui lòng chọn lại ngày theo định dạng hợp lệ.";
        else if (startDate.HasValue && endDate.HasValue && startDate.Value > endDate.Value)
            dateFilterError = "Ngày bắt đầu không được sau ngày kết thúc.";

        var loans = new List<BookLoan>();
        var allLoans = await GetLoansQuery(id, null, null).ToListAsync(cancellationToken);
        if (dateFilterError is null)
        {
            loans = await GetLoansQuery(id, startDate, endDate).ToListAsync(cancellationToken);
        }

        return View(new ReaderLoanHistoryViewModel
        {
            Reader = reader,
            Loans = loans,
            OpenLoanCount = allLoans.Count(loan => loan.ReturnedAtUtc is null),
            TotalLoanCount = allLoans.Count,
            LateReturnCount = allLoans.Count(IsLateReturn),
            StartDate = startDate,
            EndDate = endDate,
            DateFilterError = dateFilterError
        });
    }

    [HttpGet]
    public async Task<IActionResult> ExportCsv(
        int id, DateOnly? startDate = null, DateOnly? endDate = null, CancellationToken cancellationToken = default)
    {
        var reader = await db.ReaderAccounts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (reader is null) return NotFound("Không tìm thấy bạn đọc.");
        if (!ModelState.IsValid)
            return BadRequest("Ngày lọc không hợp lệ. Vui lòng chọn lại ngày theo định dạng hợp lệ.");
        if (startDate.HasValue && endDate.HasValue && startDate.Value > endDate.Value)
            return BadRequest("Ngày bắt đầu không được sau ngày kết thúc.");

        var loans = await GetLoansQuery(id, startDate, endDate).ToListAsync(cancellationToken);
        var csv = new StringBuilder();
        AppendCsvRow(csv, "Mã phiếu", "Mã bạn đọc", "Mã vạch bản sao", "Tên sách", "Ngày mượn", "Hạn trả", "Ngày trả thực tế", "Trạng thái");

        var today = HoldPickupExpirySchedule.GetBusinessDate(DateTime.UtcNow);
        foreach (var loan in loans)
        {
            var isIncidentClosed = loan.CloseReason is BookLoanCloseReason.Lost or BookLoanCloseReason.Damaged;
            var returnDate = isIncidentClosed ? null : GetLocalReturnDate(loan.ReturnedAtUtc);
            var status = loan.CloseReason switch
            {
                BookLoanCloseReason.Lost => "Đã đóng - mất",
                BookLoanCloseReason.Damaged => "Đã đóng - hỏng nặng",
                _ => returnDate.HasValue
                    ? returnDate.Value > loan.DueDate ? "Đã trả trễ hạn" : "Đã trả đúng hạn"
                    : loan.DueDate < today ? "Quá hạn, chưa trả" : "Đang mượn"
            };
            AppendCsvRow(csv,
                loan.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                reader.StudentOrStaffCode,
                loan.BookCopy?.CopyCode ?? string.Empty,
                loan.Book?.Title ?? string.Empty,
                loan.LoanDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                loan.DueDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                returnDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                status);
        }

        var safeReaderCode = System.Text.RegularExpressions.Regex.Replace(reader.StudentOrStaffCode, "[^A-Za-z0-9_-]", "_").Trim('_', '-');
        if (string.IsNullOrWhiteSpace(safeReaderCode)) safeReaderCode = reader.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var fileName = $"lich-su-muon-tra_{safeReaderCode}_{DateTime.Now:yyyyMMdd}.csv";
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        return File(bytes, "text/csv; charset=utf-8", fileName);
    }

    [HttpGet("/api/readers/{id:int}/loan-history")]
    public async Task<IActionResult> GetHistoryApi(
        int id, DateOnly? startDate = null, DateOnly? endDate = null, CancellationToken cancellationToken = default)
    {
        var reader = await db.ReaderAccounts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (reader is null) return NotFound(new { message = "Không tìm thấy bạn đọc." });
        if (!ModelState.IsValid || (startDate.HasValue && endDate.HasValue && startDate.Value > endDate.Value))
            return BadRequest(new { message = "Khoảng ngày lọc không hợp lệ." });

        var allLoans = await GetLoansQuery(id, null, null).ToListAsync(cancellationToken);
        var loans = await GetLoansQuery(id, startDate, endDate).ToListAsync(cancellationToken);
        return Ok(new
        {
            readerId = reader.Id,
            readerName = reader.FullName,
            openLoanCount = allLoans.Count(loan => loan.ReturnedAtUtc is null),
            totalLoanCount = allLoans.Count,
            lateReturnCount = allLoans.Count(IsLateReturn),
            items = loans.Select(loan => new
            {
                loanId = loan.Id,
                bookTitle = loan.Book?.Title,
                copyBarcode = loan.BookCopy?.CopyCode,
                loanDate = loan.LoanDate,
                dueDate = loan.DueDate,
                returnedAtUtc = loan.ReturnedAtUtc,
                isReturned = loan.IsReturned,
                closeReason = loan.CloseReason
            })
        });
    }

    private IQueryable<BookLoan> GetLoansQuery(int readerId, DateOnly? startDate, DateOnly? endDate)
    {
        var query = db.BookLoans.AsNoTracking().Where(loan => loan.ReaderAccountId == readerId);
        if (startDate.HasValue) query = query.Where(loan => loan.LoanDate >= startDate.Value);
        if (endDate.HasValue) query = query.Where(loan => loan.LoanDate <= endDate.Value);

        return query.Include(loan => loan.Book).Include(loan => loan.BookCopy)
            .OrderByDescending(loan => loan.LoanDate)
            .ThenByDescending(loan => loan.Id);
    }

    private static bool IsLateReturn(BookLoan loan)
    {
        var returnedDate = GetLocalReturnDate(loan.ReturnedAtUtc);
        return returnedDate.HasValue && returnedDate.Value > loan.DueDate;
    }

    private static DateOnly? GetLocalReturnDate(DateTime? returnedAtUtc) => returnedAtUtc.HasValue
        ? DateOnly.FromDateTime(HoldPickupExpirySchedule.GetVietnamLocalTime(returnedAtUtc.Value))
        : null;

    private static void AppendCsvRow(StringBuilder csv, params string[] fields)
    {
        for (var index = 0; index < fields.Length; index++)
        {
            if (index > 0) csv.Append(',');
            csv.Append('"').Append(fields[index].Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }
        csv.Append("\r\n");
    }
}
