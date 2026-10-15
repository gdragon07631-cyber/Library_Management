using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed class WorkingScheduleService(ApplicationDbContext db) : IWorkingScheduleService
{
    private static readonly DayOfWeek[] OrderedDays =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    public async Task<IReadOnlyList<WeeklyWorkingSchedule>> GetWeeklySchedulesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSevenDaysAsync(cancellationToken);
        var schedules = await db.WeeklyWorkingSchedules.ToListAsync(cancellationToken);
        return schedules.OrderBy(schedule => Array.IndexOf(OrderedDays, schedule.DayOfWeek)).ToList();
    }

    public async Task<WeeklyWorkingScheduleOutcome> UpdateWeeklyScheduleAsync(
        DayOfWeek dayOfWeek, bool isOpen, string? note, CancellationToken cancellationToken = default)
    {
        if (!OrderedDays.Contains(dayOfWeek))
            return new(false, "Thứ trong tuần không hợp lệ.");

        await EnsureSevenDaysAsync(cancellationToken);
        var schedule = await db.WeeklyWorkingSchedules
            .SingleAsync(item => item.DayOfWeek == dayOfWeek, cancellationToken);
        if (note?.Length > 500) return new(false, "Ghi chú không được vượt quá 500 ký tự.");

        schedule.IsOpen = isOpen;
        schedule.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        await db.SaveChangesAsync(cancellationToken);
        return new(true, Schedule: schedule);
    }

    public async Task<IReadOnlyList<HolidayClosure>> GetHolidayClosuresAsync(CancellationToken cancellationToken = default) =>
        await db.HolidayClosures.AsNoTracking().OrderBy(holiday => holiday.HolidayDate).ToListAsync(cancellationToken);

    public Task<HolidayClosure?> GetHolidayClosureByIdAsync(int id, CancellationToken cancellationToken = default) =>
        db.HolidayClosures.AsNoTracking().FirstOrDefaultAsync(holiday => holiday.Id == id, cancellationToken);

    public async Task<HolidayClosureOutcome> CreateHolidayClosureAsync(
        DateOnly holidayDate, string reason, string? note, CancellationToken cancellationToken = default)
    {
        var validationError = ValidateHoliday(reason, note);
        if (validationError != null) return new(false, validationError);
        if (await db.HolidayClosures.AnyAsync(holiday => holiday.HolidayDate == holidayDate, cancellationToken))
            return new(false, $"Ngày {holidayDate:dd/MM/yyyy} đã có trong danh sách ngày nghỉ.");

        var holiday = new HolidayClosure
        {
            HolidayDate = holidayDate,
            Reason = reason.Trim(),
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
        };
        db.HolidayClosures.Add(holiday);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return new(false, $"Ngày {holidayDate:dd/MM/yyyy} đã có trong danh sách ngày nghỉ.");
        }
        return new(true, Holiday: holiday);
    }

    public async Task<HolidayClosureOutcome> UpdateHolidayClosureAsync(
        int id, DateOnly holidayDate, string reason, string? note, CancellationToken cancellationToken = default)
    {
        var holiday = await db.HolidayClosures.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (holiday == null) return new(false, "Không tìm thấy ngày nghỉ.");
        var validationError = ValidateHoliday(reason, note);
        if (validationError != null) return new(false, validationError);
        if (await db.HolidayClosures.AnyAsync(item => item.Id != id && item.HolidayDate == holidayDate, cancellationToken))
            return new(false, $"Ngày {holidayDate:dd/MM/yyyy} đã có trong danh sách ngày nghỉ.");

        holiday.HolidayDate = holidayDate;
        holiday.Reason = reason.Trim();
        holiday.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return new(false, $"Ngày {holidayDate:dd/MM/yyyy} đã có trong danh sách ngày nghỉ.");
        }
        return new(true, Holiday: holiday);
    }

    public async Task<HolidayClosureOutcome> DeleteHolidayClosureAsync(int id, CancellationToken cancellationToken = default)
    {
        var holiday = await db.HolidayClosures.FindAsync([id], cancellationToken);
        if (holiday == null) return new(false, "Không tìm thấy ngày nghỉ.");
        db.HolidayClosures.Remove(holiday);
        await db.SaveChangesAsync(cancellationToken);
        return new(true, Holiday: holiday);
    }

    public async Task<LibraryOpeningStatus> GetStatusForDateAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var holiday = await db.HolidayClosures.AsNoTracking()
            .FirstOrDefaultAsync(item => item.HolidayDate == date, cancellationToken);
        if (holiday != null)
            return new(false, true, holiday.Reason);

        await EnsureSevenDaysAsync(cancellationToken);
        var schedule = await db.WeeklyWorkingSchedules.AsNoTracking()
            .SingleAsync(item => item.DayOfWeek == date.DayOfWeek, cancellationToken);
        return new(schedule.IsOpen, false, schedule.Note);
    }

    public async Task<IReadOnlySet<DateOnly>> GetOpenDatesAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default)
    {
        if (fromInclusive > toInclusive) return new HashSet<DateOnly>();

        await EnsureSevenDaysAsync(cancellationToken);
        var openByDay = await db.WeeklyWorkingSchedules.AsNoTracking()
            .ToDictionaryAsync(schedule => schedule.DayOfWeek, schedule => schedule.IsOpen, cancellationToken);
        var holidays = await db.HolidayClosures.AsNoTracking()
            .Where(holiday => holiday.HolidayDate >= fromInclusive && holiday.HolidayDate <= toInclusive)
            .Select(holiday => holiday.HolidayDate)
            .ToHashSetAsync(cancellationToken);

        var openDates = new HashSet<DateOnly>();
        for (var date = fromInclusive; date <= toInclusive; date = date.AddDays(1))
            if (openByDay[date.DayOfWeek] && !holidays.Contains(date)) openDates.Add(date);
        return openDates;
    }

    public async Task<IReadOnlySet<DateOnly>> GetConfiguredOpenDatesAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default)
    {
        if (fromInclusive > toInclusive) return new HashSet<DateOnly>();

        var schedules = await db.WeeklyWorkingSchedules.AsNoTracking().ToListAsync(cancellationToken);
        var days = Enum.GetValues<DayOfWeek>();
        if (schedules.Count != days.Length || days.Any(day => schedules.Count(item => item.DayOfWeek == day) != 1))
            throw new InvalidOperationException("Lịch mở cửa chưa được khai báo đủ bảy ngày; chưa thể tính ngày trễ theo lịch thư viện.");

        var openByDay = schedules.ToDictionary(item => item.DayOfWeek, item => item.IsOpen);
        var closures = await db.HolidayClosures.AsNoTracking()
            .Where(item => item.HolidayDate >= fromInclusive && item.HolidayDate <= toInclusive)
            .Select(item => item.HolidayDate)
            .ToHashSetAsync(cancellationToken);

        var openDates = new HashSet<DateOnly>();
        for (var date = fromInclusive; date <= toInclusive; date = date.AddDays(1))
            if (openByDay[date.DayOfWeek] && !closures.Contains(date)) openDates.Add(date);
        return openDates;
    }

    public async Task<DateOnly> AdjustLoanDueDateAsync(DateOnly proposedDate, CancellationToken cancellationToken = default)
    {
        // This path deliberately reads the configured calendar without EnsureSevenDaysAsync:
        // missing days must be reported instead of silently treating them as open.
        var schedules = await db.WeeklyWorkingSchedules.AsNoTracking().ToListAsync(cancellationToken);
        var days = Enum.GetValues<DayOfWeek>();
        if (schedules.Count != days.Length || days.Any(day => schedules.Count(item => item.DayOfWeek == day) != 1))
            throw new InvalidOperationException("Lịch ngày mở cửa chưa được cấu hình đầy đủ (cần đúng một thiết lập cho mỗi ngày trong tuần).");

        var openByDay = schedules.ToDictionary(item => item.DayOfWeek, item => item.IsOpen);
        var holidays = (await db.HolidayClosures.AsNoTracking().ToListAsync(cancellationToken))
            .Select(item => item.HolidayDate).ToHashSet();
        return DueDateAdjuster.AdjustDueDate(proposedDate,
            date => !holidays.Contains(date) && openByDay[date.DayOfWeek]);
    }

    private async Task EnsureSevenDaysAsync(CancellationToken cancellationToken)
    {
        var configuredDays = await db.WeeklyWorkingSchedules.Select(schedule => schedule.DayOfWeek).ToListAsync(cancellationToken);
        var missingSchedules = OrderedDays.Where(day => !configuredDays.Contains(day))
            .Select(day => new WeeklyWorkingSchedule { DayOfWeek = day, IsOpen = true }).ToList();
        if (missingSchedules.Count == 0) return;
        db.WeeklyWorkingSchedules.AddRange(missingSchedules);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            db.ChangeTracker.Clear();
        }
    }

    private static string? ValidateHoliday(string reason, string? note)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "Vui lòng nhập tên hoặc lý do nghỉ.";
        if (reason.Trim().Length > 150) return "Tên/lý do nghỉ không được vượt quá 150 ký tự.";
        if (note?.Length > 500) return "Ghi chú không được vượt quá 500 ký tự.";
        return null;
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };
}
