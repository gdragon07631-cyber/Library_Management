namespace Project.Services;

/// <summary>Raw email transport for durable hold notifications; history is owned by the outbox processor.</summary>
public interface IHoldNotificationEmailTransport : IEmailSender
{
}
