namespace Project.Services;

public sealed class HoldPickupReminderOptions
{
    public const string SectionName = "HoldPickupReminder";
    public const string DefaultSendTime = "08:00";

    public string SendTimeLocal { get; set; } = DefaultSendTime;
    public int RetryIntervalSeconds { get; set; } = 300;
}
