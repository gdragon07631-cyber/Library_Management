using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class HoldPickupExpirySchedulingTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;

    public HoldPickupExpirySchedulingTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public void VietnamScheduleConverts0230AndSelectsCorrectBusinessDate()
    {
        var scheduledUtc = UtcForVietnam(2026, 10, 11, 0, 30);

        Assert.Equal(new DateOnly(2026, 10, 11), HoldPickupExpirySchedule.GetBusinessDate(scheduledUtc));
        Assert.True(HoldPickupExpirySchedule.IsDue(scheduledUtc));
        Assert.Equal(new DateTime(2026, 10, 11, 0, 30, 0), HoldPickupExpirySchedule.GetVietnamLocalTime(scheduledUtc));
        Assert.Equal(UtcForVietnam(2026, 10, 12, 0, 30), HoldPickupExpirySchedule.GetNextRunUtc(scheduledUtc));
    }

    [Fact]
    public void SqlServerMigrationIsDiscoverableAndGeneratesWithoutConnectingToDatabase()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=localhost;Database=LibraryMigrationCheck;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        using var sqlServerDb = new ApplicationDbContext(options);

        Assert.Contains("20261011120000_AddHoldPickupExpiryRuns", sqlServerDb.Database.GetMigrations());
        var script = sqlServerDb.GetService<IMigrator>().GenerateScript();
        Assert.Contains("CREATE UNIQUE INDEX [IX_HoldPickupExpiryRuns_BusinessDate]", script);
        Assert.Contains("CREATE TABLE [HoldPickupExpiryRuns]", script);
    }

    [Fact]
    public async Task DoesNotRunBefore0030AndCatchesUpForCurrentDateAfterRestart()
    {
        var clock = new MutableTimeProvider(UtcForVietnam(2026, 10, 11, 0, 29));
        var scheduler = CreateScheduler(clock, new ResultExpiryService(new HoldPickupExpiryRunResult(0, 0, 0, 0, [])));

        Assert.Equal(HoldPickupScheduledRunOutcome.NotDue, await scheduler.RunScheduledIfDueAsync());
        Assert.Empty(await db.HoldPickupExpiryRuns.ToListAsync());

        clock.SetUtcNow(UtcForVietnam(2026, 10, 11, 8, 0));
        Assert.Equal(HoldPickupScheduledRunOutcome.Succeeded, await scheduler.RunScheduledIfDueAsync());
        var run = await db.HoldPickupExpiryRuns.SingleAsync();
        Assert.Equal(new DateOnly(2026, 10, 11), run.BusinessDate);
        Assert.Equal(HoldPickupExpiryRunStatus.Succeeded, run.Status);
    }

    [Fact]
    public async Task ASuccessfulBusinessDateCannotBeRunTwiceAndNextDateCanRun()
    {
        var clock = new MutableTimeProvider(UtcForVietnam(2026, 10, 11, 0, 30));
        var scheduler = CreateScheduler(clock, new ResultExpiryService(new HoldPickupExpiryRunResult(4, 2, 1, 1, [])));

        Assert.Equal(HoldPickupScheduledRunOutcome.Succeeded, await scheduler.RunScheduledIfDueAsync());
        Assert.Equal(HoldPickupScheduledRunOutcome.AlreadySucceeded, await scheduler.RunScheduledIfDueAsync());
        var firstDay = await db.HoldPickupExpiryRuns.SingleAsync();
        Assert.Equal(4, firstDay.CheckedCount);
        Assert.Equal(2, firstDay.CancelledCount);

        clock.SetUtcNow(UtcForVietnam(2026, 10, 12, 0, 30));
        Assert.Equal(HoldPickupScheduledRunOutcome.Succeeded, await scheduler.RunScheduledIfDueAsync());
        Assert.Equal(2, await db.HoldPickupExpiryRuns.CountAsync());
    }

    [Fact]
    public async Task ActiveDailyRunLockPreventsAnotherWorkerFromStarting()
    {
        var now = UtcForVietnam(2026, 10, 11, 0, 30);
        var clock = new MutableTimeProvider(now);
        db.HoldPickupExpiryRuns.Add(new HoldPickupExpiryRun
        {
            BusinessDate = new DateOnly(2026, 10, 11),
            StartedAtUtc = now.AddMinutes(-1),
            Status = HoldPickupExpiryRunStatus.Running
        });
        await db.SaveChangesAsync();
        var expiry = new ResultExpiryService(new HoldPickupExpiryRunResult(0, 0, 0, 0, []));
        var scheduler = CreateScheduler(clock, expiry);

        Assert.Equal(HoldPickupScheduledRunOutcome.InProgress, await scheduler.RunScheduledIfDueAsync());
        Assert.Equal(HoldPickupExpiryRunStatus.Running, (await db.HoldPickupExpiryRuns.SingleAsync()).Status);
        Assert.Equal(0, expiry.CallCount);
    }

    [Fact]
    public async Task FailedDailyRunCanRetryWithoutLosingActualPartialCounts()
    {
        var clock = new MutableTimeProvider(UtcForVietnam(2026, 10, 11, 0, 30));
        var expiry = new ResultExpiryService(
            new(3, 2, 1, 1, ["One hold could not be processed."]),
            new(1, 0, 0, 0, []));
        var scheduler = CreateScheduler(clock, expiry);

        Assert.Equal(HoldPickupScheduledRunOutcome.Failed, await scheduler.RunScheduledIfDueAsync());
        Assert.Equal(HoldPickupScheduledRunOutcome.Succeeded, await scheduler.RunScheduledIfDueAsync());
        var run = await db.HoldPickupExpiryRuns.SingleAsync();
        Assert.Equal(HoldPickupExpiryRunStatus.Succeeded, run.Status);
        Assert.Equal(4, run.CheckedCount);
        Assert.Equal(2, run.CancelledCount);
        Assert.Null(run.ErrorMessage);
    }

    private HoldPickupExpirySchedulingService CreateScheduler(MutableTimeProvider clock, IHoldPickupExpiryService expiry) =>
        new(db, expiry, clock, NullLogger<HoldPickupExpirySchedulingService>.Instance);

    private static DateTime UtcForVietnam(int year, int month, int day, int hour, int minute) =>
        TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(new DateTime(year, month, day, hour, minute, 0), DateTimeKind.Unspecified),
            HoldPickupExpirySchedule.VietnamTimeZone);

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    private sealed class MutableTimeProvider(DateTime utcNow) : TimeProvider
    {
        private DateTime utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        public override DateTimeOffset GetUtcNow() => new(utcNow);
        public void SetUtcNow(DateTime value) => utcNow = DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    private sealed class ResultExpiryService(params HoldPickupExpiryRunResult[] results) : IHoldPickupExpiryService
    {
        private int callCount;
        public int CallCount => callCount;

        public Task<int> ExpireOverdueAsync(DateTime nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<HoldPickupExpiryRunResult> ProcessOverdueAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            var index = Math.Min(callCount++, results.Length - 1);
            return Task.FromResult(results[index]);
        }
    }
}
