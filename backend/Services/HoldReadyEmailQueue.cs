using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Project.Models;

namespace Project.Services;

/// <summary>Creates the durable email work item in the same SaveChanges/transaction as a hold assignment.</summary>
public static class HoldReadyEmailQueue
{
    public static EmailSendHistory Create(BookHold hold, DateTime occurredAtUtc)
    {
        var eventKey = $"hold-ready|{hold.Id}|{occurredAtUtc.Ticks}";
        return new EmailSendHistory
        {
            DeduplicationKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(eventKey))),
            ReaderAccountId = hold.ReaderAccountId,
            HoldId = hold.Id,
            Recipient = hold.ReaderAccount?.Email?.Trim() ?? string.Empty,
            NotificationType = "HoldReady",
            Subject = "Th\u00F4ng b\u00E1o s\u00E1ch \u0111\u00E3 s\u1EB5n s\u00E0ng nh\u1EADn",
            StartedAtUtc = occurredAtUtc,
            Status = "Queued"
        };
    }
}
