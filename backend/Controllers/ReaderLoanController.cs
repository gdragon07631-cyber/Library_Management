using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

public sealed class ReaderLoanController(
    ApplicationDbContext db,
    IBookLoanService loans,
    IDataProtectionProvider dataProtectionProvider) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var readerId = await GetReaderIdAsync(ct);
        if (readerId <= 0) return RedirectToAction("Login", "ReaderRegistration");

        var readerLoans = await db.BookLoans.AsNoTracking()
            .Where(loan => loan.ReaderAccountId == readerId && loan.ReturnedAtUtc == null)
            .Include(loan => loan.Book)
            .Include(loan => loan.BookCopy)
            .Include(loan => loan.ReaderAccount).ThenInclude(reader => reader!.LibraryCard)
                .ThenInclude(card => card!.LibraryCardType)
            .OrderByDescending(loan => loan.CreatedAtUtc)
            .ToListAsync(ct);
        return View(readerLoans);
    }

    [HttpGet]
    public async Task<IActionResult> History(int page = 1, CancellationToken ct = default)
    {
        var readerId = await GetReaderIdAsync(ct);
        if (readerId <= 0) return RedirectToAction("Login", "ReaderRegistration");

        const int pageSize = 20;
        page = Math.Max(1, page);
        var query = db.BookLoans.AsNoTracking()
            .Where(loan => loan.ReaderAccountId == readerId && loan.ReturnedAtUtc != null);
        var totalItems = await query.CountAsync(ct);
        var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling(totalItems / (double)pageSize);
        if (totalPages > 0) page = Math.Min(page, totalPages);

        var loans = await query.Include(loan => loan.Book).Include(loan => loan.BookCopy)
            .OrderByDescending(loan => loan.ReturnedAtUtc).ThenByDescending(loan => loan.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return View(new ReaderLoanHistoryPageViewModel
        {
            Loans = loans, Page = page, PageSize = pageSize, TotalItems = totalItems
        });
    }

    [HttpGet("/api/reader/loans/{id:long}")]
    public async Task<IActionResult> DetailsApi(long id, CancellationToken ct = default)
    {
        var readerId = await GetReaderIdAsync(ct);
        if (readerId <= 0) return Unauthorized(new { message = "Vui lòng đăng nhập để xem phiếu mượn." });

        var loan = await db.BookLoans.AsNoTracking()
            .Where(item => item.Id == id && item.ReaderAccountId == readerId)
            .Select(item => new ReaderLoanDetailsResponse(item.Id, item.Book!.Title,
                item.BookCopy == null ? null : item.BookCopy.CopyCode, item.LoanDate, item.DueDate,
                item.ReturnedAtUtc != null, item.ReturnedAtUtc))
            .SingleOrDefaultAsync(ct);
        return loan == null ? NotFound() : Ok(loan);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Renew(long id, CancellationToken ct = default)
    {
        var readerId = await GetReaderIdAsync(ct);
        if (readerId <= 0) return RedirectToAction("Login", "ReaderRegistration");

        var belongsToReader = await db.BookLoans.AsNoTracking()
            .AnyAsync(loan => loan.Id == id && loan.ReaderAccountId == readerId, ct);
        if (!belongsToReader) return NotFound();

        var result = await loans.RenewAsync(id, DateOnly.FromDateTime(DateTime.Today), ct);
        TempData[result.IsSuccess ? "SuccessMessage" : "ErrorMessage"] = result.IsSuccess
            ? $"Gia hạn thành công. Hạn trả mới: {result.Loan!.DueDate:dd/MM/yyyy}. Số lần gia hạn: {result.Loan.RenewalCount} / {result.Loan.ReaderAccount!.LibraryCard!.LibraryCardType!.MaxRenewals}."
            : result.ErrorMessage;
        return RedirectToAction(nameof(Index));
    }

    private Task<int> GetReaderIdAsync(CancellationToken ct) =>
        ReaderSessionCookies.GetReaderIdAsync(HttpContext, dataProtectionProvider,
            (id, token) => db.ReaderAccounts.SingleOrDefaultAsync(reader => reader.Id == id, token), ct);
}
