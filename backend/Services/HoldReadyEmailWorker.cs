using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed class HoldReadyEmailWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<HoldReadyEmailWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProcessingLease = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessOneAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Hold email worker iteration failed."); }

            try { await Task.Delay(PollInterval, timeProvider, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task ProcessOneAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var item = await db.EmailSendHistories.AsNoTracking()
            .Where(x => (x.NotificationType == "HoldReady" || x.NotificationType == "HoldPickupLastDay" || x.NotificationType == "HoldExpired") &&
                ((x.Status == "Queued" && x.Attempts < 5) ||
                 (x.Status == "Retrying" && x.Attempts < 5 && x.CompletedAtUtc <= now.AddSeconds(-30)) ||
                 (x.Status == "Processing" && x.CompletedAtUtc <= now - ProcessingLease)))
            .OrderBy(x => x.StartedAtUtc).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        if (item is null) return;

        if (item.Status == "Processing" && item.Attempts >= 5)
        {
            await db.EmailSendHistories.Where(x => x.Id == item.Id && x.Status == "Processing" && x.CompletedAtUtc == item.CompletedAtUtc)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "Failed")
                    .SetProperty(x => x.ErrorMessage, "Tiến trình dừng trong lần thử cuối; cần nhân viên kiểm tra trước khi gửi lại.")
                    .SetProperty(x => x.CompletedAtUtc, now), cancellationToken);
            return;
        }

        // Compare status and timestamp so multiple worker instances cannot claim the same row.
        var claimed = await db.EmailSendHistories.Where(x => x.Id == item.Id && x.Status == item.Status &&
                x.CompletedAtUtc == item.CompletedAtUtc)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "Processing")
                .SetProperty(x => x.CompletedAtUtc, now)
                .SetProperty(x => x.Attempts, x => x.Attempts + 1), cancellationToken);
        if (claimed != 1) return;

        try
        {
            var hold = await db.BookHolds.AsNoTracking().Include(x => x.Book).Include(x => x.ReaderAccount)
                .Include(x => x.BookCopy).ThenInclude(x => x!.Shelf).ThenInclude(x => x!.Warehouse)
                .FirstOrDefaultAsync(x => x.Id == item.HoldId, cancellationToken);
            if (item.NotificationType == "HoldExpired")
            {
                await SendExpiredHoldAsync(db, scope.ServiceProvider.GetRequiredService<SmtpEmailSender>(), item, hold, cancellationToken);
                return;
            }

            if (hold is null || !BookHoldStatus.WaitingPickupStatuses.Contains(hold.Status) ||
                hold.BookCopy is null || hold.Book is null || hold.ReaderAccount is null ||
                hold.PickupDeadlineUtc is null || string.IsNullOrWhiteSpace(hold.Book.Title) ||
                string.IsNullOrWhiteSpace(hold.BookCopy.Shelf?.Name) || string.IsNullOrWhiteSpace(hold.BookCopy.Shelf.Warehouse?.Name) ||
                !new EmailAddressAttribute().IsValid(hold.ReaderAccount.Email))
                throw new InvalidOperationException("Thiếu thông tin hợp lệ của đơn, sách, email, hạn nhận hoặc địa điểm nhận.");

            var isLastDayReminder = item.NotificationType == "HoldPickupLastDay";
            if (isLastDayReminder && HoldPickupExpirySchedule.GetBusinessDate(hold.PickupDeadlineUtc.Value) !=
                HoldPickupExpirySchedule.GetBusinessDate(timeProvider.GetUtcNow().UtcDateTime))
                throw new InvalidOperationException("Đơn không còn trong ngày cuối cùng nhận sách; đã bỏ qua email nhắc.");

            var location = $"{hold.BookCopy.Shelf.Warehouse.Name} – {hold.BookCopy.Shelf.Name}";
            var deadline = HoldPickupExpirySchedule.GetVietnamLocalTime(hold.PickupDeadlineUtc.Value)
                .ToString("dd/MM/yyyy HH:mm");
            var greeting = $"<p>Xin chào {WebUtility.HtmlEncode(hold.ReaderAccount.FullName)},</p>";
            var introduction = isLastDayReminder
                ? "<p>Hôm nay là ngày cuối cùng để nhận sách theo thời hạn giữ hiện tại.</p>"
                : "<p>Sách bạn đã đặt giữ hiện đã sẵn sàng để nhận.</p>";
            var body = greeting + introduction + "<ul>" +
                $"<li>Tên sách: {WebUtility.HtmlEncode(hold.Book.Title)}</li>" +
                $"<li>Mã đơn đặt giữ: {hold.Id}</li><li>Hạn cuối nhận sách: {deadline} (giờ Việt Nam)</li>" +
                $"<li>Địa điểm nhận sách: {WebUtility.HtmlEncode(location)}</li></ul>" +
                "<p>Vui lòng đến nhận sách trước hạn cuối nêu trên để tránh đơn đặt giữ bị hủy do quá hạn.</p>" +
                "<p>Trân trọng,<br/>Thư viện.</p>";

            await scope.ServiceProvider.GetRequiredService<SmtpEmailSender>()
                .SendAsync(hold.ReaderAccount.Email, item.Subject, body, cancellationToken);
            await MarkSentAsync(db, item.Id, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            await MarkFailedAttemptAsync(db, item, exception, cancellationToken);
        }
    }

    private async Task SendExpiredHoldAsync(Project.Data.ApplicationDbContext db, SmtpEmailSender sender,
        EmailSendHistory item, BookHold? hold, CancellationToken cancellationToken)
    {
        if (hold is null || hold.Status != BookHoldStatus.Cancelled ||
            hold.CancellationReason != HoldPickupExpiryService.ExpiredReason || hold.CancelledAtUtc is null ||
            item.EventOccurredAtUtc != hold.CancelledAtUtc || hold.ReaderAccount is null ||
            string.IsNullOrWhiteSpace(item.BookTitleSnapshot) || item.PickupDeadlineUtcSnapshot is null ||
            !new EmailAddressAttribute().IsValid(item.Recipient))
            throw new InvalidOperationException("Không đủ dữ liệu để xác nhận đơn bị hủy do quá hạn hoặc tạo nội dung email chính xác.");

        var body = HoldExpiredEmailTemplate.Build(hold.ReaderAccount.FullName, hold.Id, item.BookTitleSnapshot,
            item.PickupDeadlineUtcSnapshot.Value, hold.CancelledAtUtc.Value, hold.CancellationReason);
        await sender.SendAsync(item.Recipient, item.Subject, body, cancellationToken);
        await MarkSentAsync(db, item.Id, cancellationToken);
    }

    private async Task MarkSentAsync(Project.Data.ApplicationDbContext db, long historyId, CancellationToken cancellationToken) =>
        await db.EmailSendHistories.Where(x => x.Id == historyId).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, "Sent").SetProperty(x => x.CompletedAtUtc, timeProvider.GetUtcNow().UtcDateTime)
            .SetProperty(x => x.ErrorMessage, (string?)null), cancellationToken);

    private async Task MarkFailedAttemptAsync(Project.Data.ApplicationDbContext db, EmailSendHistory item,
        Exception exception, CancellationToken cancellationToken)
    {
        var attempts = item.Attempts + 1;
        var status = attempts >= 5 ? "Failed" : "Retrying";
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
        if (message.Length > 1000) message = message[..1000];
        await db.EmailSendHistories.Where(x => x.Id == item.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, status).SetProperty(x => x.CompletedAtUtc, timeProvider.GetUtcNow().UtcDateTime)
            .SetProperty(x => x.ErrorMessage, message), CancellationToken.None);
        logger.LogWarning("Hold email event {HistoryId} failed on attempt {Attempt}.", item.Id, attempts);
    }
}
