using Project.Models;

namespace Project.Services;

public sealed record WeeklyWorkingScheduleOutcome(bool IsSuccess, string? ErrorMessage = null, WeeklyWorkingSchedule? Schedule = null);
public sealed record HolidayClosureOutcome(bool IsSuccess, string? ErrorMessage = null, HolidayClosure? Holiday = null);
public sealed record LibraryOpeningStatus(bool IsOpen, bool IsHolidayClosure, string? Note = null);

public interface IWorkingScheduleService
{
    Task<IReadOnlyList<WeeklyWorkingSchedule>> GetWeeklySchedulesAsync(CancellationToken cancellationToken = default);
    Task<WeeklyWorkingScheduleOutcome> UpdateWeeklyScheduleAsync(DayOfWeek dayOfWeek, bool isOpen, string? note, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HolidayClosure>> GetHolidayClosuresAsync(CancellationToken cancellationToken = default);
    Task<HolidayClosure?> GetHolidayClosureByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<HolidayClosureOutcome> CreateHolidayClosureAsync(DateOnly holidayDate, string reason, string? note, CancellationToken cancellationToken = default);
    Task<HolidayClosureOutcome> UpdateHolidayClosureAsync(int id, DateOnly holidayDate, string reason, string? note, CancellationToken cancellationToken = default);
    Task<HolidayClosureOutcome> DeleteHolidayClosureAsync(int id, CancellationToken cancellationToken = default);
    Task<LibraryOpeningStatus> GetStatusForDateAsync(DateOnly date, CancellationToken cancellationToken = default);
    Task<IReadOnlySet<DateOnly>> GetOpenDatesAsync(DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default);
    /// <summary>Reads the configured calendar without creating defaults; used for fee calculation.</summary>
    Task<IReadOnlySet<DateOnly>> GetConfiguredOpenDatesAsync(DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default);
    Task<DateOnly> AdjustLoanDueDateAsync(DateOnly proposedDate, CancellationToken cancellationToken = default);
}
