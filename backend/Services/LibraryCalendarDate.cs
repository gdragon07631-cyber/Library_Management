namespace Project.Services;

public static class LibraryCalendarDate
{
    public static DateOnly Today()
    {
        var utcNow = DateTime.UtcNow;
        return DateFromUtc(utcNow);
    }

    public static DateOnly DateFromUtc(DateTime utcDateTime)
    {
        var utc = utcDateTime.Kind == DateTimeKind.Utc
            ? utcDateTime
            : DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); }
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));
    }
}
