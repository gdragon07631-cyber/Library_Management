using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed record LoanDueReminderItem(long LoanId, string BookTitle, DateOnly DueDate, string Status);
public sealed record LoanDueReminderResult(int ScannedLoans, int EligibleLoans, int ReadersProcessed, int EmailsSent, int EmailsFailed);

public interface ILoanDueReminderService
{
    Task<LoanDueReminderResult> RunAsync(DateOnly? businessDate = null, CancellationToken cancellationToken = default);
}

public sealed class LoanDueReminderService(
    ApplicationDbContext db, IEmailSender emailSender, TimeProvider timeProvider,
    ILogger<LoanDueReminderService> logger) : ILoanDueReminderService
{
    private static readonly SemaphoreSlim RunLock = new(1, 1);

    public async Task<LoanDueReminderResult> RunAsync(DateOnly? businessDate = null, CancellationToken cancellationToken = default)
    {
        await RunLock.WaitAsync(cancellationToken);
        var started = timeProvider.GetUtcNow();
        try
        {
            var today = businessDate ?? HoldPickupExpirySchedule.GetBusinessDate(started.UtcDateTime);
            var scanned = await db.BookLoans.AsNoTracking()
                .CountAsync(x => x.ReturnedAtUtc == null && x.CloseReason == null, cancellationToken);
            var candidates = await db.BookLoans.AsNoTracking()
                .Where(x => x.ReturnedAtUtc == null && x.CloseReason == null &&
                    (x.DueDate == today || x.DueDate == today.AddDays(2)))
                .Select(x => new { x.Id, x.ReaderAccountId, ReaderName = x.ReaderAccount!.FullName,
                    ReaderEmail = x.ReaderAccount.Email, x.DueDate, BookTitle = x.Book!.Title })
                .OrderBy(x => x.ReaderAccountId).ThenBy(x => x.DueDate).ThenBy(x => x.Id)
                .ToListAsync(cancellationToken);

            var groups = candidates.GroupBy(x => x.ReaderAccountId).ToList();
            var sent = 0; var failed = 0; var processed = 0;
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var email = group.First().ReaderEmail?.Trim();
                if (string.IsNullOrWhiteSpace(email) || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email))
                {
                    failed++;
                    logger.LogWarning("Due reminder skipped reader {ReaderId}: missing or invalid email", group.Key);
                    continue;
                }
                processed++;
                var subject = $"[Thư viện] Nhắc hạn trả sách - {today:dd/MM/yyyy}";
                var body = BuildHtml(group.First().ReaderName, group.Select(x => new LoanDueReminderItem(
                    x.Id, x.BookTitle, x.DueDate, x.DueDate == today ? "Đến hạn hôm nay" : "Còn 2 ngày")));
                try
                {
                    await emailSender.SendAsync(email, subject, body, cancellationToken);
                    sent++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    logger.LogError(ex, "Due reminder email failed for reader {ReaderId}", group.Key);
                }
            }

            var result = new LoanDueReminderResult(scanned, candidates.Count, processed, sent, failed);
            logger.LogInformation("Due reminder run at {StartedAtUtc}: scanned {Scanned}, eligible {Eligible}, readers {Readers}, sent {Sent}, failed {Failed}",
                started, result.ScannedLoans, result.EligibleLoans, result.ReadersProcessed, result.EmailsSent, result.EmailsFailed);
            return result;
        }
        finally { RunLock.Release(); }
    }

    public static string BuildHtml(string readerName, IEnumerable<LoanDueReminderItem> items)
    {
        var rows = new StringBuilder();
        foreach (var item in items)
            rows.Append("<tr><td>").Append(EscapeHtml(item.BookTitle)).Append("</td><td>")
                .Append(item.DueDate.ToString("dd/MM/yyyy")).Append("</td><td>")
                .Append(EscapeHtml(item.Status)).Append("</td><td>#")
                .Append(item.LoanId).Append("</td></tr>");
        return $"<p>Xin chào {EscapeHtml(readerName)},</p><p>Thư viện xin nhắc bạn về các sách đang mượn có hạn trả hôm nay hoặc còn 2 ngày đến hạn.</p>" +
            "<table border=\"1\" cellpadding=\"6\" cellspacing=\"0\"><thead><tr><th>Tên sách</th><th>Hạn trả</th><th>Trạng thái</th><th>Mã phiếu</th></tr></thead><tbody>" + rows +
            "</tbody></table><p>Vui lòng sắp xếp trả sách đúng hạn theo quy định của thư viện.</p><p>Trân trọng,<br>Thư viện</p>";
    }

    private static string EscapeHtml(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
             .Replace("<", "&lt;", StringComparison.Ordinal)
             .Replace(">", "&gt;", StringComparison.Ordinal)
             .Replace("\"", "&quot;", StringComparison.Ordinal)
             .Replace("'", "&#39;", StringComparison.Ordinal);
}

public sealed class LoanDueReminderWorker(IServiceScopeFactory scopeFactory, TimeProvider timeProvider,
    ILogger<LoanDueReminderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var utcNow = timeProvider.GetUtcNow().UtcDateTime;
            var local = HoldPickupExpirySchedule.GetVietnamLocalTime(utcNow);
            var today = DateOnly.FromDateTime(local);
            var target = TimeOnly.FromDateTime(local) < new TimeOnly(7, 0) ? today : today.AddDays(1);
            var delay = HoldPickupExpirySchedule.ToUtc(target, new TimeOnly(7, 0)) - utcNow;
            try { await Task.Delay(delay, timeProvider, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ILoanDueReminderService>().RunAsync(null, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Daily loan due reminder run failed"); }
        }
    }
}
