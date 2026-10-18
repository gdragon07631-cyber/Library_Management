using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed record OverdueReminderResult(int EligibleLoans, int ReadersProcessed, int EmailsSent, int EmailsFailed);
public interface IOverdueReminderService
{
    Task<OverdueReminderResult> RunAsync(DateOnly? today = null, CancellationToken cancellationToken = default);
}

public sealed class OverdueReminderService(ApplicationDbContext db, IEmailSender email, IWorkingScheduleService calendar,
    TimeProvider clock, ILogger<OverdueReminderService> logger) : IOverdueReminderService
{
    private static readonly int[] Milestones = [1, 7, 30];
    private static readonly SemaphoreSlim ProcessLock = new(1, 1);

    public async Task<OverdueReminderResult> RunAsync(DateOnly? today = null, CancellationToken cancellationToken = default)
    {
        await ProcessLock.WaitAsync(cancellationToken);
        try
        {
            var date = today ?? HoldPickupExpirySchedule.GetBusinessDate(clock.GetUtcNow().UtcDateTime);
            var policy = await db.LoanPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.Id == LoanPolicy.SingletonId, cancellationToken)
                ?? throw new InvalidOperationException("Chưa cấu hình mức phí trễ hạn trong chính sách mượn.");
            if (policy.DailyOverdueFee < 0) throw new InvalidOperationException("Mức phí trễ hạn không hợp lệ.");
            var candidates = await db.BookLoans.AsNoTracking()
                .Where(x => x.ReturnedAtUtc == null && x.CloseReason == null && x.DueDate < date &&
                    (x.DueDate == date.AddDays(-1) || x.DueDate == date.AddDays(-7) || x.DueDate == date.AddDays(-30)))
                .Select(x => new { x.Id, x.ReaderAccountId, Name = x.ReaderAccount!.FullName, Email = x.ReaderAccount.Email,
                    x.DueDate, Title = x.Book!.Title })
                .ToListAsync(cancellationToken);
            var groups = candidates.GroupBy(x => x.ReaderAccountId).ToList();
            var sent = 0; var failed = 0; var processed = 0;
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var emailAddress = group.First().Email?.Trim();
                var dueItems = group.Select(x => (Loan: x, Days: date.DayNumber - x.DueDate.DayNumber))
                    .Where(x => Milestones.Contains(x.Days)).ToList();
                var eligible = new List<(long Id, int Days)>();
                var existingLogs = await db.OverdueReminderLogs
                    .Where(x => dueItems.Select(item => item.Loan.Id).Contains(x.BookLoanId))
                    .ToDictionaryAsync(x => new { x.BookLoanId, x.MilestoneDays }, cancellationToken);
                foreach (var item in dueItems)
                {
                    if (!existingLogs.TryGetValue(new { BookLoanId = item.Loan.Id, MilestoneDays = item.Days }, out var prior) ||
                        prior.Status == "Failed") eligible.Add((item.Loan.Id, item.Days));
                }
                if (eligible.Count == 0) continue;
                if (string.IsNullOrWhiteSpace(emailAddress) || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(emailAddress))
                {
                    failed++; logger.LogWarning("Overdue reminder skipped reader {ReaderId}: invalid email", group.Key); continue;
                }

                var runItems = dueItems.Where(item => eligible.Any(e => e.Id == item.Loan.Id && e.Days == item.Days)).ToList();
                var reservations = eligible.Select(item =>
                {
                    if (existingLogs.TryGetValue(new { BookLoanId = item.Id, MilestoneDays = item.Days }, out var existing))
                    {
                        existing.Status = "Sending"; existing.ErrorMessage = null;
                        existing.BusinessDate = date; existing.SentAtUtc = clock.GetUtcNow().UtcDateTime;
                        return existing;
                    }
                    return new OverdueReminderLog { BookLoanId = item.Id, MilestoneDays = item.Days,
                        ReaderAccountId = group.Key, BusinessDate = date, SentAtUtc = clock.GetUtcNow().UtcDateTime, Status = "Sending" };
                }).ToList();
                try
                {
                    foreach (var reservation in reservations)
                        if (db.Entry(reservation).State == EntityState.Detached) db.OverdueReminderLogs.Add(reservation);
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    db.ChangeTracker.Clear(); failed++; continue;
                }
                processed++;
                try
                {
                    var minDate = runItems.Min(x => x.Loan.DueDate).AddDays(1);
                    var openDates = await calendar.GetConfiguredOpenDatesAsync(minDate, date, cancellationToken);
                    var items = runItems.Select(x =>
                    {
                        var fee = OverdueFeeCalculator.Calculate(x.Loan.DueDate, date, policy.DailyOverdueFee, openDates);
                        return new OverdueEmailItem(x.Loan.Title, x.Loan.Id, x.Loan.DueDate, x.Days, policy.DailyOverdueFee, fee.EstimatedFee);
                    }).ToList();
                    var body = BuildHtml(group.First().Name, items);
                    await email.SendAsync(emailAddress, $"[Thư viện] Nhắc trả sách quá hạn - {date:dd/MM/yyyy}", body, cancellationToken);
                    foreach (var log in reservations) log.Status = "Sent";
                    await db.SaveChangesAsync(cancellationToken);
                    sent++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    foreach (var log in reservations) { log.Status = "Failed"; log.ErrorMessage = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message; }
                    await db.SaveChangesAsync(CancellationToken.None);
                    failed++; logger.LogError(ex, "Overdue reminder failed for reader {ReaderId}", group.Key);
                }
            }
            logger.LogInformation("Overdue reminder {Date}: eligible {Eligible}, readers {Readers}, sent {Sent}, failed {Failed}",
                date, candidates.Count, processed, sent, failed);
            return new(candidates.Count, processed, sent, failed);
        }
        finally { ProcessLock.Release(); }
    }

    private sealed record OverdueEmailItem(string Title, long LoanId, DateOnly DueDate, int DaysLate, decimal DailyRate, decimal Fee);
    private static string BuildHtml(string name, IReadOnlyCollection<OverdueEmailItem> items)
    {
        var rows = new StringBuilder();
        foreach (var item in items)
            rows.Append("<tr><td>").Append(WebUtility.HtmlEncode(item.Title)).Append("</td><td>")
                .Append(item.DueDate.ToString("dd/MM/yyyy")).Append("</td><td>").Append(item.DaysLate)
                .Append("</td><td>").Append(item.DailyRate.ToString("N0", CultureInfo.GetCultureInfo("vi-VN")))
                .Append(" đ</td><td>").Append(item.Fee.ToString("N0", CultureInfo.GetCultureInfo("vi-VN")))
                .Append(" đ</td><td>#").Append(item.LoanId).Append("</td></tr>");
        var sum = items.Sum(x => x.Fee);
        return $"<p>Xin chào {WebUtility.HtmlEncode(name)},</p><p>Thư viện xin nhắc các phiếu mượn sau đang quá hạn. Phí dưới đây chỉ là ước tính, không phải khoản công nợ mới được ghi nhận.</p>" +
            "<table border=\"1\" cellpadding=\"6\" cellspacing=\"0\"><thead><tr><th>Tên sách</th><th>Hạn trả</th><th>Số ngày trễ</th><th>Phí/ngày</th><th>Phí tạm tính</th><th>Mã phiếu</th></tr></thead><tbody>" + rows +
            $"</tbody></table><p>Tổng phí phạt tạm tính: {sum.ToString("N0", CultureInfo.GetCultureInfo("vi-VN"))} đ.</p><p>Vui lòng trả sách hoặc liên hệ thư viện để được hỗ trợ.</p><p>Trân trọng,<br>Thư viện</p>";
    }
}
