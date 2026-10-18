using Microsoft.AspNetCore.Mvc;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

[StaffOnly(AccountRoles.Librarian)]
public sealed class FeeReceiptController(FeeReceiptService receipts) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Details(long paymentId, CancellationToken cancellationToken = default)
    {
        var result = await receipts.GetAsync(paymentId, cancellationToken);
        if (result.Receipt is not null) return View(result.Receipt);
        Response.StatusCode = result.PaymentExists ? StatusCodes.Status409Conflict : StatusCodes.Status404NotFound;
        return View("Unavailable", result.Error);
    }
}
