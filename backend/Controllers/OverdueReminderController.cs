using Microsoft.AspNetCore.Mvc;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

[StaffOnly(AccountRoles.Librarian, AccountRoles.LibraryManager, AccountRoles.SystemAdmin)]
public sealed class OverdueReminderController(IOverdueReminderService reminders) : Controller
{
    [HttpPost("api/overdue-reminders/run"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(CancellationToken cancellationToken) =>
        Ok(await reminders.RunAsync(null, cancellationToken));
}
