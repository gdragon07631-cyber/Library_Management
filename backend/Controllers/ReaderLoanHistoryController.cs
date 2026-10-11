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
    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken = default)
    {
        var reader = await db.ReaderAccounts.AsNoTracking()
            .Include(item => item.LibraryCard)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (reader is null) return NotFound("Không tìm thấy bạn đọc.");

        var loans = await db.BookLoans.AsNoTracking()
            .Where(loan => loan.ReaderAccountId == id)
            .Include(loan => loan.Book)
            .OrderByDescending(loan => loan.LoanDate)
            .ThenByDescending(loan => loan.Id)
            .ToListAsync(cancellationToken);

        return View(new ReaderLoanHistoryViewModel
        {
            Reader = reader,
            Loans = loans,
            OpenLoanCount = loans.Count(loan => !loan.IsReturned),
            TotalLoanCount = loans.Count,
            LateReturnCount = null
        });
    }
}
