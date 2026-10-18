using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

/// <summary>Claims and sends one durable hold email without creating a second history row.</summary>
public sealed class HoldEmailOutboxProcessor(
    ApplicationDbContext db,
    IHoldNotificationEmailTransport sender,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<HoldEmailOutboxProcessor> logger)
{
    private static readonly TimeSpan ProcessingLease = TimeSpan.FromMinutes(2);

    public async Task<bool> ProcessOneAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var item = await db.EmailSendHistories.AsNoTracking()
            .Where(x => (x.NotificationType == "HoldReady" || x.NotificationType == "HoldPickupLastDay" || x.NotificationType == "HoldExpired") &&
                ((x.Status == "Queued" && x.Attempts < 5) ||
                 (x.Status == "Retrying" && x.Attempts < 5 && x.CompletedAtUtc <= now.AddSeconds(-30)) ||
                 (x.Status == "Processing" && x.CompletedAtUtc <= now - ProcessingLease)))
            .OrderBy(x => x.StartedAtUtc).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        if (item is null) return false;

        if (item.Status == "Processing" && item.Attempts >= 5)
        {
            await UpdateStatusAsync(item.Id, "Failed", now, "Worker stopped during final send attempt; manual review required.", cancellationToken);
            return true;
        }

        // The conditional update is the cross-process claim; only one worker can own this attempt.
        var claimed = await db.EmailSendHistories.Where(x => x.Id == item.Id && x.Status == item.Status &&
                x.CompletedAtUtc == item.CompletedAtUtc)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "Processing")
                .SetProperty(x => x.CompletedAtUtc, now)
                .SetProperty(x => x.Attempts, x => x.Attempts + 1), cancellationToken);
        if (claimed != 1) return true;

        try
        {
            var hold = await db.BookHolds.AsNoTracking().Include(x => x.Book).Include(x => x.ReaderAccount)
                .Include(x => x.BookCopy).ThenInclude(x => x!.Shelf).ThenInclude(x => x!.Warehouse)
                .FirstOrDefaultAsync(x => x.Id == item.HoldId, cancellationToken);
            if (item.NotificationType == "HoldExpired")
            {
                if (hold is null || hold.Status != BookHoldStatus.Cancelled ||
                    hold.CancellationReason != HoldPickupExpiryService.ExpiredReason || hold.CancelledAtUtc is null ||
                    item.EventOccurredAtUtc != hold.CancelledAtUtc)
                    return await SkipAsync(item.Id, "Hold is no longer cancelled for the recorded overdue event.", cancellationToken);
                if (hold.ReaderAccount is null || string.IsNullOrWhiteSpace(item.BookTitleSnapshot) ||
                    item.PickupDeadlineUtcSnapshot is null || !new EmailAddressAttribute().IsValid(item.Recipient))
                    throw new InvalidOperationException("Required reader, recipient, title, or deadline snapshot is missing.");

                var expiredBody = HoldExpiredEmailTemplate.Build(hold.ReaderAccount.FullName, hold.Id, item.BookTitleSnapshot,
                    item.PickupDeadlineUtcSnapshot.Value, hold.CancelledAtUtc.Value, hold.CancellationReason,
                    GetPublicBookUrl(hold.BookId));
                await sender.SendAsync(item.Recipient, item.Subject, expiredBody, cancellationToken);
                await MarkSentAsync(item.Id, cancellationToken);
                return true;
            }

            if (hold is null || !BookHoldStatus.WaitingPickupStatuses.Contains(hold.Status))
                return await SkipAsync(item.Id, "Hold is no longer waiting for pickup.", cancellationToken);
            if (item.NotificationType == "HoldPickupLastDay" &&
                (hold.PickupDeadlineUtc is null || HoldPickupExpirySchedule.GetBusinessDate(hold.PickupDeadlineUtc.Value) !=
                    HoldPickupExpirySchedule.GetBusinessDate(timeProvider.GetUtcNow().UtcDateTime)))
                return await SkipAsync(item.Id, "Hold is no longer eligible for a last-day reminder.", cancellationToken);
            if (hold.BookCopy?.Shelf?.Warehouse is null || hold.Book is null || hold.ReaderAccount is null ||
                hold.PickupDeadlineUtc is null || string.IsNullOrWhiteSpace(hold.Book.Title) ||
                string.IsNullOrWhiteSpace(hold.BookCopy.Shelf.Name) || string.IsNullOrWhiteSpace(hold.BookCopy.Shelf.Warehouse.Name) ||
                !new EmailAddressAttribute().IsValid(hold.ReaderAccount.Email))
                throw new InvalidOperationException("Required book, reader, recipient, deadline, or pickup location is missing.");

            // Recheck current business status immediately before handing the message to the transport.
            var stillEligible = await db.BookHolds.AsNoTracking().AnyAsync(x => x.Id == item.HoldId &&
                BookHoldStatus.WaitingPickupStatuses.Contains(x.Status) && x.PickupDeadlineUtc != null,
                cancellationToken);
            if (!stillEligible) return await SkipAsync(item.Id, "Hold changed state before sending.", cancellationToken);
            if (item.NotificationType == "HoldPickupLastDay" &&
                HoldPickupExpirySchedule.GetBusinessDate(hold.PickupDeadlineUtc.Value) !=
                HoldPickupExpirySchedule.GetBusinessDate(timeProvider.GetUtcNow().UtcDateTime))
                return await SkipAsync(item.Id, "Hold deadline is no longer today in Vietnam time.", cancellationToken);

            var body = HoldReadyEmailTemplate.Build(item.NotificationType == "HoldPickupLastDay",
                hold.ReaderAccount.FullName, hold.Id, hold.Book.Title, hold.PickupDeadlineUtc.Value,
                $"{hold.BookCopy.Shelf.Warehouse.Name} – {hold.BookCopy.Shelf.Name}");
            await sender.SendAsync(hold.ReaderAccount.Email, item.Subject, body, cancellationToken);
            await MarkSentAsync(item.Id, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var attempts = item.Attempts + 1;
            var status = attempts >= 5 ? "Failed" : "Retrying";
            var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
            if (message.Length > 1000) message = message[..1000];
            await UpdateStatusAsync(item.Id, status, timeProvider.GetUtcNow().UtcDateTime, message, CancellationToken.None);
            logger.LogWarning(exception, "Hold email event {HistoryId} failed on attempt {Attempt}.", item.Id, attempts);
            return true;
        }
    }

    private async Task MarkSentAsync(long id, CancellationToken cancellationToken) =>
        await db.EmailSendHistories.Where(x => x.Id == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, "Sent").SetProperty(x => x.CompletedAtUtc, timeProvider.GetUtcNow().UtcDateTime)
            .SetProperty(x => x.ErrorMessage, (string?)null), cancellationToken);

    private async Task<bool> SkipAsync(long id, string reason, CancellationToken cancellationToken)
    {
        await UpdateStatusAsync(id, "Skipped", timeProvider.GetUtcNow().UtcDateTime, reason, cancellationToken);
        return true;
    }

    private string? GetPublicBookUrl(int bookId)
    {
        var configured = configuration["PublicBaseUrl"]?.Trim();
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp)) return null;
        return new Uri(baseUri, $"/Book/Details/{bookId}").AbsoluteUri;
    }

    private Task<int> UpdateStatusAsync(long id, string status, DateTime completedAtUtc, string? error,
        CancellationToken cancellationToken) => db.EmailSendHistories.Where(x => x.Id == id)
        .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, status)
            .SetProperty(x => x.CompletedAtUtc, completedAtUtc).SetProperty(x => x.ErrorMessage, error), cancellationToken);
}
