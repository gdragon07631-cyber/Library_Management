using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Project.Data;
using Project.Models;
using Project.Services;
using System.Net.Mail;

namespace Project.Tests;

public sealed class RetryingEmailSenderTests
{
    [Fact]
    public async Task StopsAfterTransientFailureThenSuccessAndPersistsAttemptCount()
    {
        await using var db = CreateDb();
        var transport = new FlakySender(2);
        var service = CreateService(db, transport);

        await service.SendAsync("reader@example.com", "Test", "<p>hello</p>");
        await service.SendAsync("reader@example.com", "Test", "<p>duplicate should be suppressed</p>");

        var log = await db.EmailSendHistories.SingleAsync();
        Assert.Equal(2, transport.Calls);
        Assert.Equal(2, log.Attempts);
        Assert.Equal(2, transport.Calls);
        Assert.Equal(EmailSendStatus.Sent, log.Status);
        Assert.Null(log.ErrorMessage);
        Assert.Contains("Attempt 1", log.AttemptErrors);
    }

    [Fact]
    public async Task StopsAfterInitialSendAndThreeRetriesAndPersistsFailure()
    {
        await using var db = CreateDb();
        var transport = new FlakySender(5);
        var service = CreateService(db, transport);

        await Assert.ThrowsAsync<SmtpException>(() => service.SendAsync("reader@example.com", "[Thư viện] Nhắc hạn", "<p>hello</p>"));

        var log = await db.EmailSendHistories.SingleAsync();
        Assert.Equal(4, transport.Calls);
        Assert.Equal(4, log.Attempts);
        Assert.Equal(EmailSendStatus.Failed, log.Status);
        Assert.NotEmpty(log.ErrorMessage!);
        Assert.Equal("DueReminder", log.NotificationType);
    }

    [Fact]
    public async Task DoesNotRetryPermanentConfigurationOrAddressErrors()
    {
        await using var db = CreateDb();
        var transport = new PermanentFailSender();
        var service = CreateService(db, transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync("bad", "Test", "body"));

        Assert.Equal(1, transport.Calls);
        Assert.Equal(EmailSendStatus.Failed, (await db.EmailSendHistories.SingleAsync()).Status);
    }

    private static ApplicationDbContext CreateDb() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static RetryingEmailSender CreateService(ApplicationDbContext db, IEmailSender sender)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:RetryDelaysSeconds:0"] = "0", ["Email:RetryDelaysSeconds:1"] = "0", ["Email:RetryDelaysSeconds:2"] = "0"
        }).Build();
        return new RetryingEmailSender(sender, db, config, TimeProvider.System, NullLogger<RetryingEmailSender>.Instance);
    }

    private sealed class FlakySender(int successesAfterCalls) : IEmailSender
    {
        public int Calls { get; private set; }
        public Task SendAsync(string recipient, string subject, string htmlBody, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls < successesAfterCalls) throw new SmtpException(SmtpStatusCode.ServiceNotAvailable, "Temporary SMTP issue");
            return Task.CompletedTask;
        }
    }
    private sealed class PermanentFailSender : IEmailSender
    {
        public int Calls { get; private set; }
        public Task SendAsync(string recipient, string subject, string htmlBody, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Missing SMTP configuration"); }
    }
}
