using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed record OverdueReaderExportRow(string CardCode, string FullName, string PhoneNumber,
    int OverdueCount, int MaxDaysOverdue, decimal Debt);
public sealed record OverdueReaderExportResult(byte[]? Content, string FileName, string? Error, int StatusCode);

public sealed class OverdueReaderExportService(ApplicationDbContext db, IWorkingScheduleService calendar,
    TimeProvider clock, ILogger<OverdueReaderExportService> logger)
{
    public const string Action = AuditActions.ExportOverdueReaders;
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public async Task<IReadOnlyList<OverdueReaderExportRow>> GetRowsAsync(DateOnly today, CancellationToken ct, int? libraryCardTypeId = null)
    {
        var loans = await db.BookLoans.AsNoTracking()
            .Where(l => l.ReturnedAtUtc == null && l.DueDate < today)
            .Select(l => new { l.Id, l.ReaderAccountId, l.DueDate }).ToListAsync(ct);
        var openDates = loans.Count == 0 ? new HashSet<DateOnly>()
            : await calendar.GetOpenDatesAsync(loans.Min(l => l.DueDate).AddDays(1), today, ct);
        var overdue = loans.Select(l => new { l.ReaderAccountId, Days = openDates.Count(d => d > l.DueDate) })
            .Where(l => l.Days > 0).GroupBy(l => l.ReaderAccountId)
            .ToDictionary(g => g.Key, g => (Count: g.Count(), Days: g.Max(l => l.Days)));
        var ids = overdue.Keys.ToArray();
        var readers = await db.ReaderAccounts.AsNoTracking()
            .Where(r => r.OutstandingBalance > 0 || ids.Contains(r.Id))
            .Where(r => !libraryCardTypeId.HasValue || (r.LibraryCard != null && r.LibraryCard.LibraryCardTypeId == libraryCardTypeId))
            .Select(r => new { r.Id, Card = r.LibraryCard == null ? "" : r.LibraryCard.CardCode,
                r.FullName, r.PhoneNumber, r.OutstandingBalance }).ToListAsync(ct);
        return readers.OrderBy(r => r.Card, StringComparer.Ordinal).ThenBy(r => r.Id).Select(r =>
        {
            overdue.TryGetValue(r.Id, out var metrics);
            return new OverdueReaderExportRow(r.Card, r.FullName, r.PhoneNumber, metrics.Count, metrics.Days,
                Math.Max(0m, r.OutstandingBalance));
        }).ToList();
    }

    public async Task<OverdueReaderExportResult> ExportAsync(AdminAccount staff, string ip, CancellationToken ct,
        int? minimumOverdueDays = null, int? libraryCardTypeId = null)
    {
        if (!staff.IsActive || staff.Role != AccountRoles.LibraryManager)
            return new(null, "", "Tài khoản của bạn không có quyền xuất danh sách.", 403);
        if (minimumOverdueDays < 0)
            return new(null, "", OverdueReaderExportFilter.ValidationMessage, 400);
        var cardDescription = "Loại thẻ: Tất cả";
        if (libraryCardTypeId.HasValue)
        {
            try
            {
                var type = await db.LibraryCardTypes.AsNoTracking()
                    .Where(t => t.Id == libraryCardTypeId.Value && t.IsActive)
                    .Select(t => new { t.Id, t.Name }).SingleOrDefaultAsync(ct);
                if (type is null || libraryCardTypeId <= 0)
                    return new(null, "", OverdueReaderExportFilter.CardTypeValidationMessage, 400);
                cardDescription = $"Loại thẻ: {type.Name} (ID: {type.Id})";
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Không thể xác thực loại thẻ xuất.");
                return new(null, "", "Không thể kiểm tra loại thẻ. Vui lòng thử lại.", 500);
            }
        }
        var filterDescription = minimumOverdueDays is { } threshold
            ? $"Số ngày trễ tối thiểu: {threshold} ngày"
            : "Không lọc (không áp dụng bộ lọc ngày trễ)";
        var instant = clock.GetUtcNow().UtcDateTime;
        var today = HoldPickupExpirySchedule.GetBusinessDate(instant);
        var name = $"Ban-doc-qua-han-no-phi_{today:dd-MM-yyyy}.xlsx";
        byte[]? content = null;
        var count = 0;
        var outcome = "Thất bại";
        string? error = null;
        var status = 500;
        try
        {
            var allRows = await GetRowsAsync(today, ct, libraryCardTypeId);
            var rows = minimumOverdueDays is { } minimum
                ? allRows.Where(row => row.MaxDaysOverdue >= minimum).ToList()
                : allRows;
            count = rows.Count;
            if (count == 0)
            {
                outcome = "Không có dữ liệu";
                error = "Không có dữ liệu phù hợp với điều kiện đã chọn";
                status = 422;
            }
            else
            {
                content = OverdueReaderWorkbook.Create(rows);
                outcome = "Thành công";
                status = 200;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Không thể chuẩn bị tệp xuất bạn đọc.");
            error = "Không thể tạo tệp Excel. Vui lòng thử lại.";
        }
        var entry = AuditLogService.Create(staff.Email, Action,
            $"{cardDescription}; {filterDescription}; Kết quả: {outcome}; Số dòng: {count}; Mã nhân sự: {staff.Id}", ip);
        entry.OccurredAtUtc = instant;
        db.AuditLogs.Add(entry);
        try { await db.SaveChangesAsync(ct); }
        catch (Exception ex)
        {
            db.Entry(entry).State = EntityState.Detached;
            logger.LogError(ex, "Không thể lưu nhật ký xuất bạn đọc.");
            return new(null, name, "Không thể lưu nhật ký xuất. Vui lòng thử lại.", 500);
        }
        return new(content, name, error, status);
    }
}
