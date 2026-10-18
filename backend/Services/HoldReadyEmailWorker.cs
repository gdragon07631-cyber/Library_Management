using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed class HoldReadyEmailWorker(IServiceScopeFactory scopeFactory, ILogger<HoldReadyEmailWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Hold-ready email worker iteration failed."); }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private async Task ProcessOneAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTime.UtcNow;
        var item = await db.EmailSendHistories.AsNoTracking()
            .Where(x => x.NotificationType == "HoldReady" && x.Attempts < 5 &&
                (x.Status == "Queued" ||
                 (x.Status == "Retrying" && x.CompletedAtUtc <= now.AddSeconds(-30)) ||
                 (x.Status == "Processing" && x.CompletedAtUtc <= now.AddMinutes(-2))))
            .OrderBy(x => x.StartedAtUtc).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        if (item is null) return;

        // Conditional update makes only one worker instance claim this event.
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
            if (hold is null || !BookHoldStatus.WaitingPickupStatuses.Contains(hold.Status) ||
                hold.BookCopy is null || hold.Book is null || hold.ReaderAccount is null ||
                hold.PickupDeadlineUtc is null || string.IsNullOrWhiteSpace(hold.Book.Title) ||
                string.IsNullOrWhiteSpace(hold.BookCopy.Shelf?.Name) || string.IsNullOrWhiteSpace(hold.BookCopy.Shelf.Warehouse?.Name) ||
                !new EmailAddressAttribute().IsValid(hold.ReaderAccount.Email))
                throw new InvalidOperationException("Thiếu thông tin hợp lệ của đơn, sách, email, hạn nhận hoặc địa điểm nhận.");

            var location = $"{hold.BookCopy.Shelf.Warehouse.Name} – {hold.BookCopy.Shelf.Name}";
            var deadline = HoldPickupExpirySchedule.GetVietnamLocalTime(hold.PickupDeadlineUtc.Value).ToString("dd/MM/yyyy HH:mm");
            var body = $"<p>Xin chào {WebUtility.HtmlEncode(hold.ReaderAccount.FullName)},</p>" +
                "<p>Sách bạn đã đặt giữ hiện đã sẵn sàng để nhận.</p><ul>" +
                $"<li>Tên sách: {WebUtility.HtmlEncode(hold.Book.Title)}</li>" +
                $"<li>Mã đơn đặt giữ: {hold.Id}</li><li>Hạn cuối nhận sách: {deadline} (giờ Việt Nam)</li>" +
                $"<li>Địa điểm nhận sách: {WebUtility.HtmlEncode(location)}</li></ul>" +
                "<p>Vui lòng đến nhận sách trước hạn cuối nêu trên.</p><p>Trân trọng,<br/>Thư viện.</p>";
            var sender = scope.ServiceProvider.GetRequiredService<SmtpEmailSender>();
            await sender.SendAsync(hold.ReaderAccount.Email, item.Subject, body, cancellationToken);
            await db.EmailSendHistories.Where(x => x.Id == item.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, "Sent").SetProperty(x => x.CompletedAtUtc, DateTime.UtcNow)
                .SetProperty(x => x.ErrorMessage, (string?)null), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var attempts = item.Attempts + 1;
            var status = attempts >= 5 ? "Failed" : "Retrying";
            var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
            if (message.Length > 1000) message = message[..1000];
            await db.EmailSendHistories.Where(x => x.Id == item.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, status).SetProperty(x => x.CompletedAtUtc, DateTime.UtcNow)
                .SetProperty(x => x.ErrorMessage, message), CancellationToken.None);
            logger.LogWarning("Hold-ready email event {HistoryId} failed on attempt {Attempt}.", item.Id, attempts);
        }
    }

}
