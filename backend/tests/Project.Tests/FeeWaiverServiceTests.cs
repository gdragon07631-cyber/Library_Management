using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Project.Controllers;
using Project.Data;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class FeeWaiverServiceTests
{
    [Fact]
    public async Task CreateRequest_RequiresReasonAndPositiveAmountWithinOutstanding()
    {
        await using var db = CreateInMemoryDb();
        var (fee, librarian, _) = await SeedAsync(db);
        var service = new FeeWaiverService(db);

        Assert.False((await service.CreateRequestAsync(fee.Id, 100m, "   ", librarian.Id)).IsSuccess);
        Assert.False((await service.CreateRequestAsync(fee.Id, 0m, "Lý do", librarian.Id)).IsSuccess);
        Assert.False((await service.CreateRequestAsync(fee.Id, -1m, "Lý do", librarian.Id)).IsSuccess);
        Assert.False((await service.CreateRequestAsync(fee.Id, 9_001m, "Lý do", librarian.Id)).IsSuccess);
        Assert.Empty(await db.FeeWaiverRequests.ToListAsync());
    }

    [Fact]
    public async Task CreateRequest_ValidRequestIsPendingAndDoesNotChangeDebt()
    {
        await using var db = CreateInMemoryDb();
        var (fee, librarian, _) = await SeedAsync(db);
        var service = new FeeWaiverService(db);

        var result = await service.CreateRequestAsync(fee.Id, 3_000m, "  Hoàn cảnh khó khăn  ", librarian.Id);

        var saved = await db.FeeWaiverRequests.SingleAsync();
        Assert.True(result.IsSuccess);
        Assert.Equal(FeeWaiverRequestStatus.Pending, saved.Status);
        Assert.Equal("Hoàn cảnh khó khăn", saved.Reason);
        Assert.Equal(10_000m, saved.OriginalFeeAmount);
        Assert.Equal(9_000m, saved.FeeBalanceAtRequest);
        Assert.Equal(9_000m, saved.ReaderBalanceAtRequest);
        Assert.Equal(librarian.Id, saved.RequestedByAdminAccountId);
        Assert.NotEqual(default, saved.RequestedAtUtc);
        Assert.Equal(0m, (await db.ReaderFees.AsNoTracking().SingleAsync()).WaivedAmount);
        Assert.Equal(9_000m, (await db.ReaderAccounts.AsNoTracking().SingleAsync()).OutstandingBalance);
        Assert.Single(await service.GetEligibleReceivablesAsync());
        Assert.Single(await service.GetRequestsByLibrarianAsync(librarian.Id));
        Assert.Single(await service.GetPendingRequestsAsync());
    }

    [Fact]
    public async Task Approve_UpdatesOutstandingAndKeepsOriginalFeeAndHistory()
    {
        await using var db = CreateInMemoryDb();
        var (fee, librarian, manager) = await SeedAsync(db);
        var service = new FeeWaiverService(db);
        await service.CreateRequestAsync(fee.Id, 3_000m, "Hoàn cảnh khó khăn", librarian.Id);
        var requestId = await db.FeeWaiverRequests.Select(request => request.Id).SingleAsync();

        var result = await service.DecideAsync(requestId, true, "Đã xem xét", manager.Id);

        var savedRequest = await db.FeeWaiverRequests.AsNoTracking().SingleAsync();
        var savedFee = await db.ReaderFees.AsNoTracking().SingleAsync();
        var savedReader = await db.ReaderAccounts.AsNoTracking().SingleAsync();
        Assert.True(result.IsSuccess);
        Assert.Equal(FeeWaiverRequestStatus.Approved, savedRequest.Status);
        Assert.Equal(manager.Id, savedRequest.ReviewedByAdminAccountId);
        Assert.NotNull(savedRequest.ReviewedAtUtc);
        Assert.Equal("Đã xem xét", savedRequest.ReviewNote);
        Assert.Equal(10_000m, savedFee.Amount);
        Assert.Equal(3_000m, savedFee.WaivedAmount);
        Assert.Equal(6_000m, savedFee.RemainingAmount);
        Assert.Equal(6_000m, savedReader.OutstandingBalance);
        var history = await service.GetRequestAsync(requestId);
        Assert.Equal(FeeWaiverRequestStatus.Approved, history?.Status);
        Assert.Equal("Manager Waiver", history?.ReviewedBy);
    }

    [Fact]
    public async Task Reject_KeepsDebtAndStoresDecisionHistory()
    {
        await using var db = CreateInMemoryDb();
        var (fee, librarian, manager) = await SeedAsync(db);
        var service = new FeeWaiverService(db);
        await service.CreateRequestAsync(fee.Id, 3_000m, "Lý do", librarian.Id);
        var requestId = await db.FeeWaiverRequests.Select(request => request.Id).SingleAsync();

        var result = await service.DecideAsync(requestId, false, "Chưa đủ căn cứ", manager.Id);

        var saved = await db.FeeWaiverRequests.AsNoTracking().SingleAsync();
        Assert.True(result.IsSuccess);
        Assert.Equal(FeeWaiverRequestStatus.Rejected, saved.Status);
        Assert.Equal("Chưa đủ căn cứ", saved.ReviewNote);
        Assert.Equal(manager.Id, saved.ReviewedByAdminAccountId);
        Assert.Equal(0m, (await db.ReaderFees.AsNoTracking().SingleAsync()).WaivedAmount);
        Assert.Equal(9_000m, (await db.ReaderAccounts.AsNoTracking().SingleAsync()).OutstandingBalance);
    }

    [Fact]
    public async Task ProcessedRequest_CannotBeDecidedAgain()
    {
        await using var db = CreateInMemoryDb();
        var (fee, librarian, manager) = await SeedAsync(db);
        var service = new FeeWaiverService(db);
        await service.CreateRequestAsync(fee.Id, 3_000m, "Lý do", librarian.Id);
        var requestId = await db.FeeWaiverRequests.Select(request => request.Id).SingleAsync();
        Assert.True((await service.DecideAsync(requestId, true, null, manager.Id)).IsSuccess);

        var duplicate = await service.DecideAsync(requestId, true, null, manager.Id);

        Assert.False(duplicate.IsSuccess);
        Assert.True(duplicate.IsConflict);
        Assert.Equal(3_000m, (await db.ReaderFees.AsNoTracking().SingleAsync()).WaivedAmount);
        Assert.Equal(6_000m, (await db.ReaderAccounts.AsNoTracking().SingleAsync()).OutstandingBalance);
    }

    [Fact]
    public async Task ConcurrentApproval_OnlyAppliesWaiverOnce()
    {
        var connectionString = $"Data Source=FeeWaiverConcurrent-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        var anchorOptions = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(anchor).Options;
        int feeId;
        int managerId;
        long requestId;
        await using (var seedDb = new ApplicationDbContext(anchorOptions))
        {
            await seedDb.Database.EnsureCreatedAsync();
            var (fee, librarian, manager) = await SeedAsync(seedDb);
            feeId = fee.Id;
            managerId = manager.Id;
            await new FeeWaiverService(seedDb).CreateRequestAsync(feeId, 3_000m, "Lý do", librarian.Id);
            requestId = await seedDb.FeeWaiverRequests.Select(request => request.Id).SingleAsync();
        }

        async Task<FeeWaiverOperationResult?> ApproveAsync()
        {
            try
            {
                var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connectionString).Options;
                await using var context = new ApplicationDbContext(options);
                return await new FeeWaiverService(context).DecideAsync(requestId, true, null, managerId);
            }
            catch (SqliteException) { return null; }
            catch (DbUpdateException) { return null; }
        }
        var outcomes = await Task.WhenAll(ApproveAsync(), ApproveAsync());

        await using var verifyDb = new ApplicationDbContext(anchorOptions);
        Assert.Single(outcomes, outcome => outcome?.IsSuccess == true);
        Assert.Equal(3_000m, (await verifyDb.ReaderFees.AsNoTracking().SingleAsync()).WaivedAmount);
        Assert.Equal(6_000m, (await verifyDb.ReaderAccounts.AsNoTracking().SingleAsync()).OutstandingBalance);
        Assert.Equal(FeeWaiverRequestStatus.Approved, (await verifyDb.FeeWaiverRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task ApprovalFailure_RollsBackWaiverAndBalance()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var (fee, librarian, manager) = await SeedAsync(db);
        var service = new FeeWaiverService(db);
        await service.CreateRequestAsync(fee.Id, 3_000m, "Lý do", librarian.Id);
        var requestId = await db.FeeWaiverRequests.Select(request => request.Id).SingleAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailWaiverDecision BEFORE UPDATE OF Status ON FeeWaiverRequests WHEN NEW.Status = 'Approved' BEGIN SELECT RAISE(ABORT, 'decision write failed'); END;");

        await Assert.ThrowsAsync<SqliteException>(() => service.DecideAsync(requestId, true, null, manager.Id));

        Assert.Equal(0m, (await db.ReaderFees.AsNoTracking().SingleAsync()).WaivedAmount);
        Assert.Equal(9_000m, (await db.ReaderAccounts.AsNoTracking().SingleAsync()).OutstandingBalance);
        Assert.Equal(FeeWaiverRequestStatus.Pending, (await db.FeeWaiverRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public void Controllers_RestrictRequestingAndDecisionToSpecifiedRoles()
    {
        var librarianRoles = typeof(FeeWaiverController).GetCustomAttribute<StaffOnlyAttribute>()!.Roles;
        var managerRoles = typeof(FeeWaiverReviewController).GetCustomAttribute<StaffOnlyAttribute>()!.Roles;

        Assert.Equal(new[] { AccountRoles.Librarian }, librarianRoles);
        Assert.Equal(new[] { AccountRoles.LibraryManager }, managerRoles);
    }

    private static ApplicationDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new ApplicationDbContext(options);
    }

    private static async Task<(ReaderFee Fee, AdminAccount Librarian, AdminAccount Manager)> SeedAsync(ApplicationDbContext db)
    {
        var reader = new ReaderAccount
        {
            FullName = "Reader Waiver", DateOfBirth = new DateOnly(2000, 1, 1), Email = "waiver-reader@example.test",
            PhoneNumber = "0900000000", StudentOrStaffCode = "WAIVER-READER", PasswordHash = "hash",
            Status = "Đang hoạt động", OutstandingBalance = 9_000m
        };
        var librarian = new AdminAccount
        {
            FullName = "Librarian Waiver", Email = "waiver-librarian@example.test", PasswordHash = "hash",
            Role = AccountRoles.Librarian
        };
        var manager = new AdminAccount
        {
            FullName = "Manager Waiver", Email = "waiver-manager@example.test", PasswordHash = "hash",
            Role = AccountRoles.LibraryManager
        };
        var fee = new ReaderFee
        {
            ReaderAccountId = 1, ReaderAccount = reader, Amount = 10_000m, PaidAmount = 1_000m,
            WaivedAmount = 0m, Status = "Chưa thanh toán", Description = "Test receivable"
        };
        db.AddRange(reader, librarian, manager, fee);
        await db.SaveChangesAsync();
        return (fee, librarian, manager);
    }
}
