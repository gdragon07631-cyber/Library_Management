using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public interface IHoldPickupExpiryHistoryService
{
    Task<AutoCancelledHoldHistoryPageViewModel> GetLast30DaysAsync(
        DateTime nowUtc, int page = 1, int pageSize = 25, CancellationToken cancellationToken = default);
}

public sealed class HoldPickupExpiryHistoryService(ApplicationDbContext db) : IHoldPickupExpiryHistoryService
{
    public async Task<AutoCancelledHoldHistoryPageViewModel> GetLast30DaysAsync(
        DateTime nowUtc, int page = 1, int pageSize = 25, CancellationToken cancellationToken = default)
    {
        nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var fromUtc = nowUtc.AddDays(-30);

        var query = db.BookHolds.AsNoTracking()
            .Where(hold => hold.Status == BookHoldStatus.Cancelled &&
                hold.CancelledByAdminAccountId == null &&
                hold.CancellationReason == HoldPickupExpiryService.ExpiredReason &&
                hold.CancelledAtUtc != null &&
                hold.CancelledAtUtc >= fromUtc && hold.CancelledAtUtc <= nowUtc);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(hold => hold.CancelledAtUtc)
            .ThenByDescending(hold => hold.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(hold => new AutoCancelledHoldHistoryItem
            {
                HoldId = hold.Id,
                BookTitle = hold.Book!.Title,
                ExpiredBookCopyCode = hold.ExpiredBookCopyCode,
                ReaderName = hold.ReaderAccount!.FullName,
                CancelledAtUtc = hold.CancelledAtUtc!.Value,
                CancellationReason = hold.CancellationReason!,
                Status = hold.Status,
                ExpiryCopyOutcome = hold.ExpiryCopyOutcome,
                ExpiryNextHoldId = hold.ExpiryNextHoldId
            })
            .ToListAsync(cancellationToken);

        return new AutoCancelledHoldHistoryPageViewModel
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }
}
