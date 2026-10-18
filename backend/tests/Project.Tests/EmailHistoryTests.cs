using Microsoft.EntityFrameworkCore;
using Project.Controllers;
using Project.Data;
using Project.Filters;
using Project.Models;

namespace Project.Tests;

public sealed class EmailHistoryTests
{
    [Fact]
    public async Task FiltersByReaderCodeAndPartialEmailAndPaginates()
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var reader = new ReaderAccount { FullName = "Reader", Email = "reader@example.com", PhoneNumber = "0900000000",
            StudentOrStaffCode = "BD-001", PasswordHash = "x", DateOfBirth = new DateOnly(2000, 1, 1) };
        db.ReaderAccounts.Add(reader);
        await db.SaveChangesAsync();
        for (var i = 0; i < 55; i++) db.EmailSendHistories.Add(new EmailSendHistory
        {
            ReaderAccountId = reader.Id, Recipient = "reader@example.com", Subject = "Reminder",
            NotificationType = "DueReminder", StartedAtUtc = DateTime.UtcNow.AddMinutes(-i), Status = EmailSendStatus.Sent, Attempts = 1
        });
        db.EmailSendHistories.Add(new EmailSendHistory { Recipient = "someone@example.net", Subject = "Other", NotificationType = "Other", StartedAtUtc = DateTime.UtcNow, Status = EmailSendStatus.Sent });
        await db.SaveChangesAsync();
        var controller = new EmailHistoryController(db);

        var result = Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(await controller.Index("BD-001", "reader@", 1));
        var model = Assert.IsType<EmailSendHistoryPageViewModel>(result.Model);

        Assert.Equal(2, model.TotalPages);
        Assert.Equal(50, model.Items.Count);
        Assert.All(model.Items, item => Assert.Equal("BD-001", item.ReaderCode));
        Assert.DoesNotContain(model.Items, item => item.Recipient == "someone@example.net");
    }

    [Fact]
    public void HistoryControllerIsRestrictedAtTheBackendToLibrariansAndSystemAdmins()
    {
        var attribute = typeof(EmailHistoryController).GetCustomAttributes(typeof(StaffOnlyAttribute), false)
            .Cast<StaffOnlyAttribute>().Single();
        Assert.Equal(new[] { AccountRoles.Librarian, AccountRoles.SystemAdmin }, attribute.Roles);
    }
}
