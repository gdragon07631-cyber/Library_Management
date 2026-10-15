using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class OverdueFeeWorkingScheduleTests
{
    private static readonly DateOnly Friday = new(2026, 10, 16);

    [Fact]
    public async Task FridayDue_MondayReturn_CountsOnlyConfiguredOpenDay()
    {
        Assert.Equal(DayOfWeek.Friday, Friday.DayOfWeek);
        var result = await CalculateAsync(Friday, Friday.AddDays(3));
        Assert.Equal(1, result.OverdueDays);
        Assert.Equal(2_000m, result.EstimatedFee);
    }

    [Fact]
    public async Task LatePeriod_ExcludesDeclaredHoliday()
    {
        var monday = Friday.AddDays(3);
        var result = await CalculateAsync(Friday, monday.AddDays(1), monday);
        Assert.Equal(1, result.OverdueDays);
    }

    [Fact]
    public async Task LatePeriod_ExcludesWeeklyClosedDay()
    {
        var saturday = Friday.AddDays(1);
        var result = await CalculateAsync(Friday, saturday);
        Assert.Equal(0, result.OverdueDays);
        Assert.Equal(0m, result.EstimatedFee);
    }

    [Fact]
    public async Task MultipleClosuresBetweenOpenDays_CountOnlyOpenDates()
    {
        var tuesday = Friday.AddDays(4);
        var thursday = Friday.AddDays(6);
        var result = await CalculateAsync(Friday, Friday.AddDays(7), tuesday, thursday);
        Assert.Equal(3, result.OverdueDays); // Monday, Wednesday, Friday
        Assert.Equal(6_000m, result.EstimatedFee);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task OnOrBeforeDueDate_IsNeverLate(int offset)
    {
        var result = await CalculateAsync(Friday, Friday.AddDays(offset));
        Assert.Equal(0, result.OverdueDays);
        Assert.Equal(0m, result.EstimatedFee);
    }

    [Fact]
    public async Task MissingWeeklyConfiguration_FailsClosedWithoutCreatingSchedule()
    {
        await using var db = CreateDb();
        var schedules = new WorkingScheduleService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            schedules.GetConfiguredOpenDatesAsync(Friday.AddDays(1), Friday.AddDays(3)));
        Assert.Empty(await db.WeeklyWorkingSchedules.ToListAsync());
    }

    private static async Task<OverdueFeeCalculation> CalculateAsync(
        DateOnly dueDate, DateOnly returnDate, params DateOnly[] closures)
    {
        await using var db = CreateDb();
        db.WeeklyWorkingSchedules.AddRange(Enum.GetValues<DayOfWeek>().Select(day => new WeeklyWorkingSchedule
        {
            DayOfWeek = day,
            IsOpen = day is not DayOfWeek.Saturday and not DayOfWeek.Sunday
        }));
        db.HolidayClosures.AddRange(closures.Select(date => new HolidayClosure { HolidayDate = date, Reason = "Test closure" }));
        await db.SaveChangesAsync();

        var openDates = await new WorkingScheduleService(db)
            .GetConfiguredOpenDatesAsync(dueDate.AddDays(1), returnDate);
        return OverdueFeeCalculator.Calculate(dueDate, returnDate, 2_000m, openDates);
    }

    private static ApplicationDbContext CreateDb() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
