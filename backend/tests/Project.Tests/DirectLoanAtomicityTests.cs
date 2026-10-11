using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed partial class DirectLoanTests
{
    private static DirectLoanService Direct(ApplicationDbContext context)
    {
        var schedule = new WorkingScheduleService(context);
        return new(context, new BookLoanService(context, schedule), schedule, NullLogger<DirectLoanService>.Instance);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task IdempotentReplayReturnsOriginalReceiptsAndRejectsDifferentPayload(int count)
    {
        var codes = await AddCopies(count);
        var id = Guid.NewGuid();
        var first = await service.ConfirmManyAsync("CARD01", codes, staff, requestId: id);
        Assert.True(first.IsSuccess, first.ErrorMessage);
        using var otherContext = new ApplicationDbContext(Options().Options);
        var replay = await Direct(otherContext).ConfirmManyAsync("CARD01", codes, staff, requestId: id);
        Assert.True(replay.IsSuccess, replay.ErrorMessage);
        Assert.Equal(first.Loans!.Select(l => l.Id), replay.Loans!.Select(l => l.Id));
        var conflict = await service.ConfirmManyAsync("CARD01", [codes[0]], staff, requestId: id);
        Assert.True(conflict.RequestConflict);
        Assert.Equal(count, await db.BookLoans.CountAsync());
        Assert.Equal(count, await db.DirectLoanOperationItems.CountAsync());
        Assert.NotNull((await db.DirectLoanOperations.SingleAsync()).CompletedAtUtc);
    }

    [Theory]
    [InlineData("loan", 1)]
    [InlineData("loan", 3)]
    [InlineData("loan", 5)]
    [InlineData("copy", 1)]
    [InlineData("copy", 3)]
    [InlineData("copy", 5)]
    [InlineData("hold", 1)]
    [InlineData("links", 1)]
    public async Task EveryWriteFaultRollsBackAndSameRequestCanRetry(string stage, int occurrence)
    {
        var codes = await AddCopies(5);
        var hold = await AddHoldAsync("COPY03");
        var fault = new WriteFault(stage, occurrence);
        using var context = new ApplicationDbContext(Options().AddInterceptors(fault).Options);
        var id = Guid.NewGuid();
        var result = await Direct(context).ConfirmManyAsync("CARD01", codes, staff, requestId: id);
        Assert.True(fault.Fired);
        Assert.False(result.IsSuccess);
        Assert.False(result.IsUncertain);
        await AssertNoLoanEffectsAsync(hold.Id);
        // Binding survives failure, so a different payload cannot reuse the key.
        Assert.True((await Direct(context).ConfirmManyAsync("CARD01", ["COPY01"], staff, requestId: id)).RequestConflict);
        fault.Enabled = false;
        var retry = await Direct(context).ConfirmManyAsync("CARD01", codes, staff, requestId: id);
        Assert.True(retry.IsSuccess, retry.ErrorMessage);
        Assert.Equal(5, await db.BookLoans.CountAsync());
        Assert.Equal(5, await db.DirectLoanOperationItems.CountAsync());
    }

    private async Task AssertNoLoanEffectsAsync(long holdId)
    {
        using var check = new ApplicationDbContext(Options().Options);
        Assert.Empty(await check.BookLoans.ToListAsync());
        Assert.Empty(await check.DirectLoanOperationItems.ToListAsync());
        Assert.Empty(await check.BookCopyStatusHistories.ToListAsync());
        Assert.Empty(await check.AuditLogs.ToListAsync());
        Assert.Equal(4, await check.BookCopies.CountAsync(c => c.Status == BookCopyStatus.Available));
        Assert.Equal(1, await check.BookCopies.CountAsync(c => c.Status == BookCopyStatus.OnHold));
        Assert.Equal(BookHoldStatus.Available, (await check.BookHolds.SingleAsync(h => h.Id == holdId)).Status);
        Assert.Equal(0, (await Direct(check).LookupAsync("CARD01", null)).CurrentLoans);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitFailureIsVerifiedBeforeReportingAnOutcome(bool afterCommit)
    {
        var codes = await AddCopies(5);
        var hold = await AddHoldAsync("COPY03");
        var fault = new CommitFault(afterCommit);
        using var context = new ApplicationDbContext(Options().AddInterceptors(fault).Options);
        var id = Guid.NewGuid();
        var result = await Direct(context).ConfirmManyAsync("CARD01", codes, staff, requestId: id);
        Assert.True(fault.Fired);
        Assert.False(result.IsUncertain);
        Assert.Equal(afterCommit, result.IsSuccess);
        if (afterCommit)
        {
            Assert.Equal(5, result.Loans!.Count);
            Assert.Equal(BookHoldStatus.ConvertedToLoan, (await db.BookHolds.AsNoTracking().SingleAsync()).Status);
        }
        else await AssertNoLoanEffectsAsync(hold.Id);
        fault.Enabled = false;
        Assert.True((await Direct(context).ConfirmManyAsync("CARD01", codes, staff, requestId: id)).IsSuccess);
        Assert.Equal(5, await db.BookLoans.CountAsync());
    }

    [Fact]
    public async Task UnknownCommitDoesNotClaimRollbackAndLaterVerificationFindsReceipts()
    {
        var codes = await AddCopies(3);
        var outage = new RecoveryOutage();
        var commit = new CommitFault(true, () => outage.Enabled = true);
        using var context = new ApplicationDbContext(Options().AddInterceptors(commit, outage).Options);
        var id = Guid.NewGuid();
        var result = await Direct(context).ConfirmManyAsync("CARD01", codes, staff, requestId: id);
        Assert.True(result.IsUncertain);
        Assert.DoesNotContain("Chưa có dữ liệu được ghi nhận", result.ErrorMessage!);
        Assert.Equal(3, await db.BookLoans.CountAsync());
        outage.Enabled = false; commit.Enabled = false;
        var verified = await Direct(context).VerifyAsync(id, staff.Id);
        Assert.True(verified.IsSuccess);
        Assert.Equal(3, verified.Loans!.Count);
        Assert.True((await Direct(context).ConfirmManyAsync("CARD01", codes, staff, requestId: id)).IsSuccess);
        Assert.Equal(3, await db.BookLoans.CountAsync());
    }

    [Fact]
    public async Task SameRequestConcurrentSubmissionsCreateExactlyOneBatch()
    {
        var codes = await AddCopies(3);
        var id = Guid.NewGuid();
        using var barrier = new Barrier(2);
        async Task<DirectLoanBatchOutcome> Run() => await Task.Run(async () =>
        {
            using var context = new ApplicationDbContext(Options().Options);
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            return await Direct(context).ConfirmManyAsync("CARD01", codes, staff, requestId: id);
        });
        var results = await Task.WhenAll(Run(), Run());
        Assert.Contains(results, r => r.IsSuccess);
        var replay = await service.ConfirmManyAsync("CARD01", codes, staff, requestId: id);
        Assert.True(replay.IsSuccess, replay.ErrorMessage);
        foreach (var success in results.Where(r => r.IsSuccess))
            Assert.Equal(replay.Loans!.Select(l => l.Id), success.Loans!.Select(l => l.Id));
        Assert.Equal(3, await db.BookLoans.CountAsync());
        Assert.Single(await db.DirectLoanOperations.ToListAsync());
    }

    [Fact]
    public async Task RequestStatusIsRestrictedToOriginalStaff()
    {
        var id = Guid.NewGuid();
        Assert.True((await service.ConfirmManyAsync("CARD01", ["COPY01"], staff, requestId: id)).IsSuccess);
        Assert.True((await service.VerifyAsync(id, staff.Id + 1)).RequestConflict);
    }

    [Fact]
    public void MigrationSnapshotMatchesSqlServerModelAndScriptOnlyAddsOperationTables()
    {
        using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=MigrationMetadataOnly;Integrated Security=true").Options);
        var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        var initialized = context.GetService<IModelRuntimeInitializer>().Initialize(snapshot);
        var differences = context.GetService<IMigrationsModelDiffer>().GetDifferences(
            initialized.GetRelationalModel(), context.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.True(!context.Database.HasPendingModelChanges(), string.Join("; ", differences.Select(d =>
            d.GetType().Name + ": " + System.Text.Json.JsonSerializer.Serialize(d, d.GetType()))));
        var script = context.GetService<IMigrator>().GenerateScript("20261011150000_AddHoldPickupExpiryHistoryDetails", "20261011160000_AddDirectLoanOperations");
        Assert.Contains("CREATE TABLE [DirectLoanOperations]", script);
        Assert.Contains("CREATE TABLE [DirectLoanOperationItems]", script);
        Assert.DoesNotContain("ALTER TABLE [BookLoans]", script);
        Assert.DoesNotContain("DROP TABLE", script);
    }

    private sealed class WriteFault(string stage, int occurrence) : DbCommandInterceptor
    {
        public bool Enabled { get; set; } = true;
        public bool Fired { get; private set; }
        private int seen;
        private void Check(DbCommand command)
        {
            if (!Enabled || Fired) return;
            var sql = command.CommandText.Replace("[", "").Replace("]", "").Replace("\"", "");
            var match = stage switch
            {
                "loan" => sql.Contains("INSERT INTO BookLoans"),
                "copy" => sql.Contains("UPDATE") && sql.Contains("BookCopies") && sql.Contains("Status"),
                "hold" => sql.Contains("UPDATE") && sql.Contains("BookHolds") && sql.Contains("Status"),
                "links" => sql.Contains("INSERT INTO DirectLoanOperationItems"),
                _ => false
            };
            if (match && ++seen == occurrence) { Fired = true; throw new InvalidOperationException("Simulated write failure"); }
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { Check(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) { Check(command); return ValueTask.FromResult(result); }
    }

    private sealed class CommitFault(bool after, Action? lostResponse = null) : DbTransactionInterceptor
    {
        public bool Enabled { get; set; } = true;
        public bool Fired { get; private set; }
        private void Check(DbContext? context, bool committed)
        {
            if (Enabled && !Fired && after == committed && context!.ChangeTracker.Entries<DirectLoanOperation>()
                .Any(e => e.Entity.CompletedAtUtc != null))
            { Fired = true; lostResponse?.Invoke(); throw new InvalidOperationException("Simulated commit connection loss"); }
        }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default) { Check(eventData.Context, false); return ValueTask.FromResult(result); }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default) { Check(eventData.Context, true); return Task.CompletedTask; }
    }

    private sealed class RecoveryOutage : DbConnectionInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Enabled) throw new InvalidOperationException("Simulated recovery database outage");
            return ValueTask.FromResult(result);
        }
    }
}
