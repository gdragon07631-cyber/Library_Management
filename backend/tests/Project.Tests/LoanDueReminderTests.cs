using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Tests;

public sealed class LoanDueReminderTests
{
    [Fact]
    public async Task GroupsEligibleLoansPerReaderAndSkipsClosedAndOtherDates()
    {
        await using var db = CreateDb();
        var reader = Reader("reader@example.com");
        var author = new Author { Name = "Tác giả" };
        var book = new Book { Title = "Sách <an toàn>", Author = author, AuthorId = 1 };
        db.AddRange(reader, author, book);
        await db.SaveChangesAsync();
        db.BookLoans.AddRange(
            Loan(reader.Id, book.Id, new DateOnly(2026, 10, 15)),
            Loan(reader.Id, book.Id, new DateOnly(2026, 10, 17)),
            Loan(reader.Id, book.Id, new DateOnly(2026, 10, 16)),
            Loan(reader.Id, book.Id, new DateOnly(2026, 10, 18)),
            Loan(reader.Id, book.Id, new DateOnly(2026, 10, 15), returned: true));
        await db.SaveChangesAsync();
        var sender = new FakeEmailSender();
        var service = new LoanDueReminderService(db, sender, TimeProvider.System, NullLogger<LoanDueReminderService>.Instance);

        var result = await service.RunAsync(new DateOnly(2026, 10, 15));

        Assert.Equal(1, sender.Messages.Count);
        Assert.Equal(2, result.EligibleLoans);
        Assert.Equal(1, result.EmailsSent);
        Assert.Equal(0, result.EmailsFailed);
        Assert.Contains("Còn 2 ngày", sender.Messages[0].Body);
        Assert.Contains("Đến hạn hôm nay", sender.Messages[0].Body);
        Assert.Contains("Sách &lt;an toàn&gt;", sender.Messages[0].Body);
    }

    [Fact]
    public async Task InvalidEmailAndSendFailureDoNotStopOtherReaders()
    {
        await using var db = CreateDb();
        var a = Reader("invalid"); var b = Reader("fail@example.com"); var c = Reader("ok@example.com");
        var author = new Author { Name = "Tác giả" }; var book = new Book { Title = "Sách", Author = author, AuthorId = 1 };
        db.AddRange(a, b, c, author, book); await db.SaveChangesAsync();
        foreach (var reader in new[] { a, b, c }) db.BookLoans.Add(Loan(reader.Id, book.Id, new DateOnly(2026, 10, 15)));
        await db.SaveChangesAsync();
        var sender = new FakeEmailSender { FailureAddress = "fail@example.com" };
        var service = new LoanDueReminderService(db, sender, TimeProvider.System, NullLogger<LoanDueReminderService>.Instance);

        var result = await service.RunAsync(new DateOnly(2026, 10, 15));

        Assert.Equal(1, result.EmailsSent);
        Assert.Equal(2, result.EmailsFailed);
        Assert.Single(sender.Messages);
    }

    private static ApplicationDbContext CreateDb() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ReaderAccount Reader(string email) => new()
    {
        FullName = "Bạn đọc", Email = email, PhoneNumber = "0900000000", StudentOrStaffCode = Guid.NewGuid().ToString(),
        PasswordHash = "test", DateOfBirth = new DateOnly(2000, 1, 1), Status = "Đang hoạt động"
    };

    private static BookLoan Loan(int readerId, int bookId, DateOnly due, bool returned = false) => new()
    {
        ReaderAccountId = readerId, BookId = bookId, LoanDate = due.AddDays(-7), DueDate = due,
        OriginalDueDate = due, ReturnedAtUtc = returned ? DateTime.UtcNow : null
    };

    private sealed class FakeEmailSender : IEmailSender
    {
        public List<(string Recipient, string Subject, string Body)> Messages { get; } = [];
        public string? FailureAddress { get; init; }
        public Task SendAsync(string recipient, string subject, string htmlBody, CancellationToken cancellationToken = default)
        {
            if (recipient == FailureAddress) throw new InvalidOperationException("Simulated SMTP failure");
            Messages.Add((recipient, subject, htmlBody)); return Task.CompletedTask;
        }
    }
}
