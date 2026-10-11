using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Project.Controllers;
using Project.Data;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class LoanBlockHistoryTests
{
    [Fact]
    public async Task Search_FiltersByReaderDateAndEventAndSortsNewestFirst()
    {
        await using var fixture = await Fixture.CreateAsync();
        var from = DateOnly.FromDateTime(DateTime.Today);
        var start = AuditLogService.StartOfLocalDayUtc(from);
        fixture.Db.AuditBlockLogs.AddRange(
            Entry("R-100", "Nguyen An", LoanBlockHistoryEventTypes.BlockedDebt, start.AddHours(2)),
            Entry("R-100", "Nguyen An", LoanBlockHistoryEventTypes.BlockedDebt, start.AddDays(1).AddTicks(-1)),
            Entry("R-100", "Nguyen An", LoanBlockHistoryEventTypes.OverrideUsed, start.AddHours(12)),
            Entry("R-200", "Tran Binh", LoanBlockHistoryEventTypes.BlockedDebt, start.AddHours(13)),
            Entry("R-100", "Nguyen An", LoanBlockHistoryEventTypes.BlockedDebt, start.AddDays(1)));
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.SearchAsync(new LoanBlockHistoryFilter
        {
            ReaderQuery = "R-100",
            FromDate = from,
            ToDate = from,
            EventType = LoanBlockHistoryEventTypes.BlockedDebt
        });

        Assert.Equal(2, result.Records.Count);
        Assert.All(result.Records, record => Assert.Equal("R-100", record.ReaderCode));
        Assert.All(result.Records, record => Assert.Equal(LoanBlockHistoryEventTypes.BlockedDebt, record.EventType));
        Assert.Equal(start.AddDays(1).AddTicks(-1), result.Records[0].OccurredAtUtc);
        Assert.Equal(2, result.TotalCount);
    }

    [Fact]
    public async Task Search_FindsReaderByNameAndReturnsEveryEventTypeWhenUnfiltered()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.AuditBlockLogs.AddRange(
            Entry("R-300", "Nguyen Van An", LoanBlockHistoryEventTypes.BlockedMultiple, DateTime.UtcNow),
            Entry("R-301", "Le An", LoanBlockHistoryEventTypes.OverrideFailed, DateTime.UtcNow.AddMinutes(-1)));
        await fixture.Db.SaveChangesAsync();

        var byName = await fixture.Service.SearchAsync(new LoanBlockHistoryFilter { ReaderQuery = "van an" });
        var all = await fixture.Service.SearchAsync(new LoanBlockHistoryFilter());

        Assert.Equal("R-300", Assert.Single(byName.Records).ReaderCode);
        Assert.Equal(2, all.TotalCount);
        Assert.Equal(LoanBlockHistoryEventTypes.BlockedMultiple, all.Records[0].EventType);
    }

    [Fact]
    public async Task Search_PaginatesWithoutDuplicates()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTime.UtcNow;
        fixture.Db.AuditBlockLogs.AddRange(Enumerable.Range(0, 27).Select(index =>
            Entry($"R-{index:000}", $"Reader {index}", LoanBlockHistoryEventTypes.BlockedLimit, now.AddMinutes(-index))));
        await fixture.Db.SaveChangesAsync();

        var firstPage = await fixture.Service.SearchAsync(new LoanBlockHistoryFilter { PageSize = 10 });
        var secondPage = await fixture.Service.SearchAsync(new LoanBlockHistoryFilter { Page = 2, PageSize = 10 });
        var thirdPage = await fixture.Service.SearchAsync(new LoanBlockHistoryFilter { Page = 3, PageSize = 10 });

        Assert.Equal(27, firstPage.TotalCount);
        Assert.Equal(3, firstPage.PageCount);
        Assert.Equal(10, firstPage.Records.Count);
        Assert.Equal(10, secondPage.Records.Count);
        Assert.Equal(7, thirdPage.Records.Count);
        Assert.Empty(firstPage.Records.Select(item => item.Id).Intersect(secondPage.Records.Select(item => item.Id)));
    }

    [Fact]
    public async Task GetById_ReturnsSnapshotWithoutLookingUpCurrentReaderData()
    {
        await using var fixture = await Fixture.CreateAsync();
        var entry = Entry("R-SNAPSHOT", "Reader at event time", LoanBlockHistoryEventTypes.OverrideUsed, DateTime.UtcNow);
        entry.OverrideReason = "Phê duyệt ngoại lệ";
        entry.BookLoanId = 123;
        fixture.Db.AuditBlockLogs.Add(entry);
        await fixture.Db.SaveChangesAsync();

        var found = await fixture.Service.GetByIdAsync(entry.Id);

        Assert.NotNull(found);
        Assert.Equal("Reader at event time", found.ReaderName);
        Assert.Equal("Phê duyệt ngoại lệ", found.OverrideReason);
        Assert.Equal(123, found.BookLoanId);
    }

    [Fact]
    public void ControllerAndBothHistoryEndpointsRequireLibraryManager()
    {
        var attribute = typeof(LoanBlockHistoryController).GetCustomAttributes(typeof(StaffOnlyAttribute), false)
            .Cast<StaffOnlyAttribute>().Single();

        Assert.Equal([AccountRoles.LibraryManager], attribute.Roles);
        Assert.NotNull(typeof(LoanBlockHistoryController).GetMethod(nameof(LoanBlockHistoryController.SearchApi)));
        Assert.NotNull(typeof(LoanBlockHistoryController).GetMethod(nameof(LoanBlockHistoryController.DetailsApi)));
    }

    private static AuditBlockLog Entry(string code, string name, string eventType, DateTime occurredAt) => new()
    {
        OccurredAtUtc = occurredAt,
        ReaderAccountId = 1,
        ReaderCode = code,
        ReaderName = name,
        BorrowedCount = 2,
        BorrowingLimit = 5,
        OverdueLoanCount = 0,
        OutstandingBalance = 0,
        Reasons = "Nguyên nhân đã lưu",
        Actor = "Thủ thư",
        EventType = eventType,
        Outcome = eventType.StartsWith("Override", StringComparison.Ordinal) ? "Used" : "Blocked"
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(ApplicationDbContext db)
        {
            Db = db;
            Service = new LoanBlockHistoryService(db);
        }

        public ApplicationDbContext Db { get; }
        public LoanBlockHistoryService Service { get; }

        public static Task<Fixture> CreateAsync()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return Task.FromResult(new Fixture(new ApplicationDbContext(options)));
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
