using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

/// <summary>Records each email and performs one initial SMTP send plus at most three transient retries.</summary>
public sealed class RetryingEmailSender(IEmailSender inner, ApplicationDbContext db, IConfiguration configuration,
    TimeProvider clock, ILogger<RetryingEmailSender> logger) : IEmailSender
{
    private static readonly TimeSpan[] DefaultRetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    public async Task SendAsync(string recipient, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        var started = clock.GetUtcNow().UtcDateTime;
        var businessDate = HoldPickupExpirySchedule.GetBusinessDate(started);
        var deduplicationKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{recipient.Trim().ToUpperInvariant()}|{subject.Trim()}|{businessDate:yyyy-MM-dd}")));
        if (await db.EmailSendHistories.AsNoTracking().AnyAsync(x => x.DeduplicationKey == deduplicationKey, cancellationToken))
        {
            logger.LogInformation("Suppressed duplicate email send for {Recipient} with subject {Subject}", SafeTrim(recipient, 256), SafeTrim(subject, 500));
            return;
        }
        var entry = new EmailSendHistory
        {
            DeduplicationKey = deduplicationKey,
            Recipient = SafeTrim(recipient, 256), Subject = SafeTrim(subject, 500),
            NotificationType = GetNotificationType(subject), OverdueMilestoneDays = GetMilestone(subject),
            ReaderAccountId = await FindReaderIdAsync(recipient, cancellationToken), StartedAtUtc = started,
            Status = EmailSendStatus.Sending
        };
        try
        {
            db.EmailSendHistories.Add(entry);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.EmailSendHistories.AsNoTracking().AnyAsync(x => x.DeduplicationKey == deduplicationKey, cancellationToken)) return;
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not persist email history before sending to {Recipient}", SafeTrim(recipient, 256));
            throw;
        }

        var errors = new List<string>();
        var delays = GetRetryDelays();
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            entry.Attempts = attempt;
            entry.Status = attempt == 1 ? EmailSendStatus.Sending : EmailSendStatus.Retrying;
            await SaveHistorySafelyAsync(entry, cancellationToken);
            try
            {
                await inner.SendAsync(recipient, subject, htmlBody, cancellationToken);
                entry.Status = EmailSendStatus.Sent;
                entry.ErrorMessage = null;
                entry.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
                await SaveHistorySafelyAsync(entry, cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                entry.Status = EmailSendStatus.Failed;
                entry.ErrorMessage = "Email send was cancelled.";
                entry.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
                await SaveHistorySafelyAsync(entry, CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                var message = SanitizeException(ex);
                errors.Add($"Attempt {attempt}: {message}");
                entry.AttemptErrors = JsonSerializer.Serialize(errors).Length <= 4000
                    ? JsonSerializer.Serialize(errors) : JsonSerializer.Serialize(errors)[..4000];
                var transient = IsTransient(ex);
                if (!transient || attempt == 4)
                {
                    entry.Status = EmailSendStatus.Failed;
                    entry.ErrorMessage = message.Length <= 1000 ? message : message[..1000];
                    entry.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
                    await SaveHistorySafelyAsync(entry, CancellationToken.None);
                    throw;
                }
                entry.Status = EmailSendStatus.Retrying;
                await SaveHistorySafelyAsync(entry, cancellationToken);
                var delay = delays[attempt - 1];
                if (delay > TimeSpan.Zero)
                {
                    try { await Task.Delay(delay, clock, cancellationToken); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        entry.Status = EmailSendStatus.Failed;
                        entry.ErrorMessage = "Email retry was cancelled.";
                        entry.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
                        await SaveHistorySafelyAsync(entry, CancellationToken.None);
                        throw;
                    }
                }
            }
        }
    }

    private async Task<int?> FindReaderIdAsync(string recipient, CancellationToken cancellationToken) =>
        await db.ReaderAccounts.AsNoTracking().Where(x => x.Email.ToUpper() == recipient.Trim().ToUpper())
            .Select(x => (int?)x.Id).FirstOrDefaultAsync(cancellationToken);

    private async Task SaveHistorySafelyAsync(EmailSendHistory entry, CancellationToken cancellationToken)
    {
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (Exception ex) { logger.LogError(ex, "Could not update email history {HistoryId}", entry.Id); }
    }

    private TimeSpan[] GetRetryDelays()
    {
        var configured = configuration.GetSection("Email:RetryDelaysSeconds").Get<int[]>();
        if (configured is not { Length: 3 } || configured.Any(x => x < 0 || x > 300)) return DefaultRetryDelays;
        return configured.Select(seconds => TimeSpan.FromSeconds(seconds)).ToArray();
    }

    private static string GetNotificationType(string subject)
    {
        if (subject.Contains("quá hạn", StringComparison.OrdinalIgnoreCase)) return "Overdue";
        if (subject.Contains("nhắc hạn", StringComparison.OrdinalIgnoreCase)) return "DueReminder";
        return "Other";
    }
    private static int? GetMilestone(string subject)
    {
        foreach (var day in new[] { 1, 7, 30 })
            if (subject.Contains($"ngày {day}", StringComparison.OrdinalIgnoreCase) ||
                subject.Contains($"{day} ngày", StringComparison.OrdinalIgnoreCase)) return day;
        return null;
    }
    private static bool IsTransient(Exception exception) => exception switch
    {
        SmtpFailedRecipientException => false,
        SmtpException smtp => smtp.StatusCode is SmtpStatusCode.GeneralFailure or SmtpStatusCode.ServiceNotAvailable or
            SmtpStatusCode.LocalErrorInProcessing or SmtpStatusCode.ExceededStorageAllocation,
        TimeoutException or IOException => true,
        _ => false
    };
    private static string SafeTrim(string? value, int limit)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length <= limit ? trimmed : trimmed[..limit];
    }
    private static string SanitizeException(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
        return message.Length <= 1000 ? message : message[..1000];
    }
}
