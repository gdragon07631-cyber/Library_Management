namespace Project.Services;

public static class HoldPickupReminderSchedule
{
    public static bool IsDue(DateTime utcNow, TimeOnly runTimeLocal)
    {
        var localNow = HoldPickupExpirySchedule.GetVietnamLocalTime(utcNow);
        return TimeOnly.FromDateTime(localNow) >= runTimeLocal;
    }

    public static DateTime GetNextRunUtc(DateTime utcNow, TimeOnly runTimeLocal)
    {
        var localNow = HoldPickupExpirySchedule.GetVietnamLocalTime(utcNow);
        var nextDate = DateOnly.FromDateTime(localNow);
        if (TimeOnly.FromDateTime(localNow) >= runTimeLocal) nextDate = nextDate.AddDays(1);
        return HoldPickupExpirySchedule.ToUtc(nextDate, runTimeLocal);
    }

    public static bool TryParseRunTime(string? value, out TimeOnly runTime) =>
        TimeOnly.TryParseExact(value, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out runTime);
}
