using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Filters;
using Project.Models;

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
        if (dateFilterError is null)
        {
            var query = db.BookLoans.AsNoTracking().Where(loan => loan.ReaderAccountId == id);
            if (startDate.HasValue) query = query.Where(loan => loan.LoanDate >= startDate.Value);
            if (endDate.HasValue) query = query.Where(loan => loan.LoanDate <= endDate.Value);

            loans = await query
                .Include(loan => loan.Book)
                .OrderByDescending(loan => loan.LoanDate)
                .ThenByDescending(loan => loan.Id)
                .ToListAsync(cancellationToken);
        }

        return View(new ReaderLoanHistoryViewModel
        {
            Reader = reader,
            Loans = loans,
            OpenLoanCount = loans.Count(loan => !loan.IsReturned),
            TotalLoanCount = loans.Count,
            LateReturnCount = null,
            StartDate = startDate,
            EndDate = endDate,
            DateFilterError = dateFilterError
        });
    }
}
