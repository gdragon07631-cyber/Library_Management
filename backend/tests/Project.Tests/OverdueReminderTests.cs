using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class OverdueReminderTests
{
    [Theory]
    [InlineData(1, 2_000)]
    [InlineData(7, 14_000)]
    [InlineData(30, 60_000)]
    public async Task SendsConfiguredEstimatedFeeAtMilestones(int days, decimal expectedFee)
    {
        await using var db = CreateDb();
        var author = new Author { Name = "Tác giả" };
        var book = new Book { Title = "Cơ sở dữ liệu", Author = author, AuthorId = 1 };
        var reader = new ReaderAccount { FullName = "Bạn đọc", Email = "reader@example.com", PhoneNumber = "0900000000",
            StudentOrStaffCode = Guid.NewGuid().ToString(), DateOfBirth = new DateOnly(2000, 1, 1), PasswordHash = "x", Status = "Đang hoạt động" };
        db.AddRange(author, book, reader, new LoanPolicy { Id = LoanPolicy.SingletonId, DailyOverdueFee = 2_000m });
        foreach (var day in Enum.GetValues<DayOfWeek>()) db.WeeklyWorkingSchedules.Add(new WeeklyWorkingSchedule { DayOfWeek = day, IsOpen = true });
        await db.SaveChangesAsync();
        db.BookLoans.Add(new BookLoan { BookId = book.Id, ReaderAccountId = reader.Id,
            DueDate = new DateOnly(2026, 10, 15).AddDays(-days), OriginalDueDate = new DateOnly(2026, 10, 15).AddDays(-days),
            LoanDate = new DateOnly(2026, 9, 1) });
        await db.SaveChangesAsync();
        var sender = new FakeSender();
        var service = new OverdueReminderService(db, sender, new WorkingScheduleService(db), TimeProvider.System,
            NullLogger<OverdueReminderService>.Instance);

        var result = await service.RunAsync(new DateOnly(2026, 10, 15));

        Assert.Equal(1, result.EmailsSent);
        Assert.Contains($">{days}<", sender.Body);
        Assert.Contains(expectedFee.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("vi-VN")), sender.Body);
        Assert.Single(await db.OverdueReminderLogs.Where(x => x.Status == "Sent").ToListAsync());
        Assert.Empty(await db.ReaderFees.ToListAsync());
        var secondRun = await service.RunAsync(new DateOnly(2026, 10, 15));
        Assert.Equal(0, secondRun.EmailsSent);
        Assert.Equal(1, sender.SendCount);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public async Task DoesNotSendAtNonMilestoneDays(int days)
    {
        await using var db = CreateDb();
        var author = new Author { Name = "Tác giả" }; var book = new Book { Title = "Sách", Author = author, AuthorId = 1 };
        var reader = new ReaderAccount { FullName = "Bạn đọc", Email = "reader@example.com", PhoneNumber = "0900000000",
            StudentOrStaffCode = Guid.NewGuid().ToString(), DateOfBirth = new DateOnly(2000, 1, 1), PasswordHash = "x", Status = "Đang hoạt động" };
        db.AddRange(author, book, reader, new LoanPolicy { Id = LoanPolicy.SingletonId, DailyOverdueFee = 2_000m });
        await db.SaveChangesAsync();
        db.BookLoans.Add(new BookLoan { BookId = book.Id, ReaderAccountId = reader.Id, DueDate = new DateOnly(2026, 10, 15).AddDays(-days), LoanDate = new DateOnly(2026, 9, 1) });
        await db.SaveChangesAsync();
        var sender = new FakeSender();
        var service = new OverdueReminderService(db, sender, new WorkingScheduleService(db), TimeProvider.System, NullLogger<OverdueReminderService>.Instance);

        await service.RunAsync(new DateOnly(2026, 10, 15));

        Assert.Empty(sender.Body);
    }

    private static ApplicationDbContext CreateDb() => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private sealed class FakeSender : IEmailSender
    {
        public string Body { get; private set; } = string.Empty;
        public int SendCount { get; private set; }
        public Task SendAsync(string recipient, string subject, string htmlBody, CancellationToken cancellationToken = default) { Body = htmlBody; SendCount++; return Task.CompletedTask; }
    }
}
