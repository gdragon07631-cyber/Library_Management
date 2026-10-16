using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

/// <summary>
/// S4-01 Lát 2: tính thử và xác nhận nhận trả theo lịch mở cửa thực tế,
/// và quyết định PO "không có mức phí trễ hạn mặc định".
/// </summary>
public sealed class OverdueFeeReturnFlowTests
{
    private static readonly DateOnly Friday = new(2026, 10, 16);
    private const string CopyCode = "S401-COPY-1";

    [Fact]
    public async Task Preview_FridayDue_MondayReturn_CountsOnlyMonday()
    {
        await using var db = await SeedAsync(Friday);

        var outcome = await CreateService(db).LookupByCopyCodeAsync(CopyCode, Friday.AddDays(3));

        Assert.Equal(BookCopyLoanLookupStatus.Borrowed, outcome.Status);
        Assert.Equal(Friday, outcome.DueDate);
        Assert.Equal(1, outcome.OverdueDays);
        Assert.Equal(2_000m, outcome.DailyOverdueFee);
        Assert.Equal(2_000m, outcome.EstimatedOverdueFee);
    }

    [Fact]
    public async Task Preview_IsDeterministic_AndFollowsChangedReturnDate()
    {
        await using var db = await SeedAsync(Friday);
        var service = CreateService(db);

        var first = await service.LookupByCopyCodeAsync(CopyCode, Friday.AddDays(3));
        var second = await service.LookupByCopyCodeAsync(CopyCode, Friday.AddDays(3));
        var tuesday = await service.LookupByCopyCodeAsync(CopyCode, Friday.AddDays(4));

        Assert.Equal(first, second);
        Assert.Equal(2, tuesday.OverdueDays);
        Assert.Equal(4_000m, tuesday.EstimatedOverdueFee);
    }

    [Fact]
    public async Task Preview_ExcludesDeclaredAdHocClosure()
    {
        var wednesday = Friday.AddDays(5);
        await using var db = await SeedAsync(Friday, closures: (wednesday, "Đóng cửa bảo trì hệ thống"));

        // Mon, Tue, (Wed đóng cửa), Thu
        var outcome = await CreateService(db).LookupByCopyCodeAsync(CopyCode, Friday.AddDays(6));

        Assert.Equal(3, outcome.OverdueDays);
        Assert.Equal(6_000m, outcome.EstimatedOverdueFee);
    }

    [Fact]
    public async Task Preview_ReturnOnClosedDay_DoesNotCountThatDay()
    {
        var thursday = Friday.AddDays(-1);
        await using var db = await SeedAsync(thursday);

        // Hạn thứ Năm, trả thứ Bảy (đóng cửa): chỉ thứ Sáu được tính.
        var outcome = await CreateService(db).LookupByCopyCodeAsync(CopyCode, Friday.AddDays(1));

        Assert.Equal(1, outcome.OverdueDays);
    }

    [Fact]
    public async Task Confirm_UsesSameResultAsPreviewForSameDate()
    {
        await using var db = await SeedAsync(Friday);
        var service = CreateService(db);
        var mondayNoonUtc = new DateTime(2026, 10, 19, 5, 0, 0, DateTimeKind.Utc); // 12:00 giờ Việt Nam

        var preview = await service.LookupByCopyCodeAsync(CopyCode, LibraryCalendarDate.DateFromUtc(mondayNoonUtc));
        var result = await service.ReturnAsync(preview.LoanId!.Value, mondayNoonUtc);

        Assert.True(result.IsSuccess);
        var saved = await db.BookLoans.AsNoTracking().SingleAsync();
        Assert.Equal(preview.OverdueDays, saved.OverdueDaysAtReturn);
        Assert.Equal(preview.EstimatedOverdueFee, saved.OverdueFee);
        Assert.Equal(preview.DailyOverdueFee, saved.DailyOverdueFeeAtReturn);
    }

    [Fact]
    public async Task Preview_FailsWhenDailyFeeNotDeclared()
    {
        await using var db = await SeedAsync(Friday, dailyFee: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService(db).LookupByCopyCodeAsync(CopyCode, Friday.AddDays(3)));
    }

    [Fact]
    public async Task Confirm_IsBlockedWhenDailyFeeNotDeclared_AndLoanStaysOpen()
    {
        await using var db = await SeedAsync(Friday, dailyFee: null);
        var loanId = (await db.BookLoans.SingleAsync()).Id;

        var result = await CreateService(db).ReturnAsync(loanId, DateTime.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.Contains("Chưa cấu hình chính sách phí trễ hạn", result.ErrorMessage);
        var saved = await db.BookLoans.AsNoTracking().SingleAsync();
        Assert.Null(saved.ReturnedAtUtc);
        Assert.Null(saved.CloseReason);
        Assert.Equal(BookCopyStatus.OnLoan, (await db.BookCopies.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public void NewLoanPolicy_HasNoImplicitDailyFee()
    {
        Assert.Null(new LoanPolicy().DailyOverdueFee);
        Assert.Null(new LoanPolicyViewModel().DailyOverdueFee);
    }

    private static BookLoanService CreateService(ApplicationDbContext db) => new(db, new WorkingScheduleService(db));

    private static async Task<ApplicationDbContext> SeedAsync(
        DateOnly dueDate, decimal? dailyFee = 2_000m, params (DateOnly Date, string Reason)[] closures)
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.LoanPolicies.Add(new LoanPolicy { Id = LoanPolicy.SingletonId, DailyOverdueFee = dailyFee });
        db.WeeklyWorkingSchedules.AddRange(Enum.GetValues<DayOfWeek>().Select(day => new WeeklyWorkingSchedule
        {
            DayOfWeek = day,
            IsOpen = day is not DayOfWeek.Saturday and not DayOfWeek.Sunday
        }));
        db.HolidayClosures.AddRange(closures.Select(item => new HolidayClosure { HolidayDate = item.Date, Reason = item.Reason }));

        var book = new Book { Id = 1, Title = "S4-01 test book", AuthorId = 1 };
        var reader = new ReaderAccount
        {
            Id = 1, FullName = "S4-01 Reader", DateOfBirth = new DateOnly(2000, 1, 1),
            Email = "s401@example.test", PhoneNumber = "0900000000", StudentOrStaffCode = "S401-1",
            PasswordHash = "hash", Status = "Đang hoạt động"
        };
        var copy = new BookCopy { Id = 1, BookId = book.Id, Book = book, ShelfId = 1, CopyCode = CopyCode, Status = BookCopyStatus.OnLoan };
        var loan = new BookLoan
        {
            Id = 1, BookId = book.Id, Book = book, BookCopyId = copy.Id, BookCopy = copy,
            ReaderAccountId = reader.Id, ReaderAccount = reader,
            LoanDate = dueDate.AddDays(-14), OriginalDueDate = dueDate, DueDate = dueDate
        };
        db.AddRange(book, reader, copy, loan);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return db;
    }
}

