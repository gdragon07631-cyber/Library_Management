using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed class LoanBlockHistoryService(ApplicationDbContext db) : ILoanBlockHistoryService
{
    private const int MaximumPageSize = 100;

    public async Task<LoanBlockHistoryPage> SearchAsync(
        LoanBlockHistoryFilter filter, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 10, MaximumPageSize);
        IQueryable<AuditBlockLog> query = db.AuditBlockLogs.AsNoTracking();

        if (filter.FromDate is { } fromDate)
            query = query.Where(log => log.OccurredAtUtc >= AuditLogService.StartOfLocalDayUtc(fromDate));
        if (filter.ToDate is { } toDate)
            query = query.Where(log => log.OccurredAtUtc < AuditLogService.StartOfLocalDayUtc(toDate.AddDays(1)));

        if (!string.IsNullOrWhiteSpace(filter.ReaderQuery))
        {
            var readerQuery = filter.ReaderQuery.Trim().ToUpperInvariant();
            query = query.Where(log =>
                log.ReaderCode.ToUpper().Contains(readerQuery) || log.ReaderName.ToUpper().Contains(readerQuery));
        }

        if (!string.IsNullOrWhiteSpace(filter.EventType) &&
            LoanBlockHistoryEventTypes.Options.Any(option => option.Value == filter.EventType))
        {
            query = query.Where(log => log.EventType == filter.EventType);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var records = await query
            .OrderByDescending(log => log.OccurredAtUtc).ThenByDescending(log => log.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new LoanBlockHistoryPage(records, totalCount, page, pageSize);
    }

    public Task<AuditBlockLog?> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
        db.AuditBlockLogs.AsNoTracking().SingleOrDefaultAsync(log => log.Id == id, cancellationToken);
}
