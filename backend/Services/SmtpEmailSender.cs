using System.Net;
using System.Net.Mail;

namespace Project.Services;

public sealed class SmtpEmailSender(IConfiguration configuration) : IEmailSender, IHoldNotificationEmailTransport
{
    public async Task SendAsync(string recipient, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        var settings = configuration.GetSection("Email");
        var host = settings["SmtpHost"];
        var from = settings["From"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(from) ||
            !int.TryParse(settings["SmtpPort"], out var port))
        {
            throw new InvalidOperationException("Cấu hình Email:SmtpHost, Email:SmtpPort và Email:From trước khi gửi email.");
        }

        using var message = new MailMessage(from, recipient, subject, htmlBody) { IsBodyHtml = true };
        using var client = new SmtpClient(host, port)
        {
            EnableSsl = bool.TryParse(settings["EnableSsl"], out var enableSsl) && enableSsl
        };
        var username = settings["Username"];
        var password = settings["Password"];
        if (!string.IsNullOrWhiteSpace(username))
        {
            client.Credentials = new NetworkCredential(username, password);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await client.SendMailAsync(message, cancellationToken);
    }
}
