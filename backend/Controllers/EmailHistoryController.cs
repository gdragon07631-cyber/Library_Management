using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Filters;
using Project.Models;

namespace Project.Controllers;

[StaffOnly(AccountRoles.Librarian, AccountRoles.SystemAdmin)]
public sealed class EmailHistoryController(ApplicationDbContext db) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(string? readerCode, string? email, int page = 1, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        email = string.IsNullOrWhiteSpace(email) ? null : email.Trim()[..Math.Min(email.Trim().Length, 256)];
        var query = db.EmailSendHistories.AsNoTracking().AsQueryable();
        readerCode = string.IsNullOrWhiteSpace(readerCode) ? null : readerCode.Trim()[..Math.Min(readerCode.Trim().Length, 50)];
        if (readerCode is not null)
        {
            var readerIds = db.ReaderAccounts.AsNoTracking().Where(reader => reader.StudentOrStaffCode == readerCode).Select(reader => reader.Id);
            query = query.Where(x => x.ReaderAccountId != null && readerIds.Contains(x.ReaderAccountId.Value));
        }
        if (email is not null) query = query.Where(x => x.Recipient.Contains(email));
        var count = await query.CountAsync(cancellationToken);
        page = Math.Min(page, Math.Max(1, (int)Math.Ceiling(count / 50d)));
        var items = await query.OrderByDescending(x => x.StartedAtUtc).ThenByDescending(x => x.Id)
            .Skip((page - 1) * 50).Take(50)
            .GroupJoin(db.ReaderAccounts.AsNoTracking(), log => log.ReaderAccountId, reader => (int?)reader.Id,
                (log, readers) => new { log, reader = readers.Select(x => x.StudentOrStaffCode).FirstOrDefault() })
            .Select(x => new EmailSendHistoryItem(x.log.Id, x.log.StartedAtUtc, x.log.CompletedAtUtc,
                x.log.ReaderAccountId, x.reader, x.log.Recipient, x.log.NotificationType, x.log.Subject,
                x.log.Status, x.log.Attempts, x.log.ErrorMessage, x.log.OverdueMilestoneDays) { HoldId = x.log.HoldId })
            .ToListAsync(cancellationToken);
        var holdIds = items.Where(item => item.HoldId.HasValue).Select(item => item.HoldId!.Value).Distinct().ToArray();
        if (holdIds.Length > 0)
        {
            var titles = await db.BookHolds.AsNoTracking().Where(hold => holdIds.Contains(hold.Id))
                .Select(hold => new { hold.Id, Title = hold.Book == null ? null : hold.Book.Title })
                .ToDictionaryAsync(item => item.Id, item => item.Title, cancellationToken);
            items = items.Select(item => item.HoldId is long holdId && titles.TryGetValue(holdId, out var title)
                ? item with { BookTitle = title }
                : item).ToList();
        }
        return View(new EmailSendHistoryPageViewModel
        {
            Items = items, ReaderCodeFilter = readerCode, Email = email, Page = page,
            TotalPages = Math.Max(1, (int)Math.Ceiling(count / 50d))
        });
    }
}
