using Project.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

[StaffOnly(AccountRoles.Librarian, AccountRoles.SystemAdmin, AccountRoles.LibraryManager)]
public sealed class AuthorController(
    IAuthorService authorService,
    ApplicationDbContext dbContext) : Controller
{
    // ==========================================
    // MVC VIEW ACTIONS
    // ==========================================

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken = default)
    {
        if (!await IsLibrarianSignedInAsync(cancellationToken))
            return RedirectToAction("Login", "Account", new { returnUrl = Url.Action(nameof(Index)) });

        var staff = await GetSignedInStaffAsync(cancellationToken);
        var authors = await authorService.GetAllAuthorsAsync(cancellationToken);
        var viewModel = new AuthorIndexViewModel
        {
            Authors = authors,
            NewAuthor = new CreateAuthorViewModel(),
            CanManage = IsManagerOrAdmin(staff)
        };
        return View(viewModel);
    }

    private async Task<bool> IsLibrarianSignedInAsync(CancellationToken ct)
    {
        if (!Request.Cookies.TryGetValue("admin_refresh", out var token) || string.IsNullOrWhiteSpace(token)) return false;
        var hash = TokenService.HashRefreshToken(token);
        return await dbContext.RefreshTokens.AnyAsync(item => item.TokenHash == hash && item.RevokedAtUtc == null && item.ExpiresAtUtc > DateTime.UtcNow && item.AdminAccount.IsActive && (item.AdminAccount.Role == AccountRoles.SystemAdmin || item.AdminAccount.Role == AccountRoles.LibraryManager || item.AdminAccount.Role == AccountRoles.Librarian), ct);
    }

    private async Task<AdminAccount?> GetSignedInStaffAsync(CancellationToken ct)
    {
        var audit = HttpContext.RequestServices.GetRequiredService<IAuditLogService>();
        return await audit.GetSignedInStaffAsync(Request, ct);
    }

    private static bool IsManagerOrAdmin(AdminAccount? staff) =>
        staff?.Role is AccountRoles.LibraryManager or AccountRoles.SystemAdmin;

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        CreateAuthorViewModel newAuthor,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            var authors = await authorService.GetAllAuthorsAsync(cancellationToken);
            return View(nameof(Index), new AuthorIndexViewModel
            {
                Authors = authors,
                NewAuthor = newAuthor
            });
        }

        var outcome = await authorService.CreateAuthorAsync(newAuthor.Name, newAuthor.Note, cancellationToken);
        if (!outcome.IsSuccess)
        {
            ModelState.AddModelError("NewAuthor.Name", outcome.ErrorMessage ?? "Tác giả với tên này đã tồn tại.");
            var authors = await authorService.GetAllAuthorsAsync(cancellationToken);
            return View(nameof(Index), new AuthorIndexViewModel
            {
                Authors = authors,
                NewAuthor = newAuthor
            });
        }

        TempData["SuccessMessage"] = $"Thêm tác giả \"{outcome.Author!.Name}\" thành công.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        EditAuthorViewModel model,
        CancellationToken cancellationToken = default)
    {
        var staff = await GetSignedInStaffAsync(cancellationToken);
        if (!IsManagerOrAdmin(staff))
        {
            TempData["ErrorMessage"] = "Bạn không có quyền chỉnh sửa tác giả.";
            return RedirectToAction(nameof(Index));
        }

        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = "Thông tin chỉnh sửa tác giả không hợp lệ.";
            return RedirectToAction(nameof(Index));
        }

        var outcome = await authorService.UpdateAuthorAsync(model.Id, model.Name, model.Note, cancellationToken);
        if (!outcome.IsSuccess)
        {
            TempData["ErrorMessage"] = outcome.ErrorMessage ?? "Không thể cập nhật tác giả.";
            return RedirectToAction(nameof(Index));
        }

        TempData["SuccessMessage"] = $"Cập nhật thông tin tác giả \"{outcome.Author!.Name}\" thành công.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleStatus(
        int id,
        CancellationToken cancellationToken = default)
    {
        var outcome = await authorService.ToggleAuthorStatusAsync(id, cancellationToken);
        if (!outcome.IsSuccess)
        {
            TempData["ErrorMessage"] = outcome.ErrorMessage ?? "Không tìm thấy tác giả.";
            return RedirectToAction(nameof(Index));
        }

        TempData["SuccessMessage"] = $"Đã chuyển trạng thái tác giả \"{outcome.Author!.Name}\" sang \"{outcome.Author.Status}\".";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(
        int id,
        CancellationToken cancellationToken = default)
    {
        var outcome = await authorService.DeleteAuthorAsync(id, cancellationToken);
        if (outcome.HasLinkedBooks)
        {
            TempData["ErrorMessage"] = outcome.ErrorMessage ?? "Không thể xóa tác giả này vì đã có sách liên kết.";
            return RedirectToAction(nameof(Index));
        }

        if (!outcome.IsSuccess)
        {
            TempData["ErrorMessage"] = outcome.ErrorMessage ?? "Không thể xóa tác giả.";
            return RedirectToAction(nameof(Index));
        }

        TempData["SuccessMessage"] = "Xóa tác giả thành công.";
        return RedirectToAction(nameof(Index));
    }

    // ==========================================
    // REST APIs
    // ==========================================

    /// <summary>
    /// API 1: Lấy danh sách Tác giả cho màn hình quản lý danh mục.
    /// Bao gồm: Tên tác giả, Ghi chú, Trạng thái.
    /// </summary>
    [HttpGet("api/authors")]
    public async Task<IActionResult> GetAllAuthorsApi(CancellationToken cancellationToken = default)
    {
        var authors = await authorService.GetAllAuthorsAsync(cancellationToken);
        var result = authors.Select(a => new
        {
            id = a.Id,
            name = a.Name,
            note = a.Note,
            status = a.Status,
            createdAtUtc = a.CreatedAtUtc
        });
        return Ok(result);
    }

    /// <summary>
    /// API 2: Lấy danh sách Tác giả đang hoạt động cho ô chọn khi biên mục sách.
    /// CHỈ trả về Author có Status = "Hoạt động".
    /// </summary>
    [HttpGet("api/authors/active")]
    [HttpGet("Author/ActiveAuthors")]
    public async Task<IActionResult> GetActiveAuthorsApi(CancellationToken cancellationToken = default)
    {
        var activeAuthors = await authorService.GetActiveAuthorsAsync(cancellationToken);
        var result = activeAuthors.Select(a => new
        {
            id = a.Id,
            name = a.Name,
            note = a.Note,
            status = a.Status
        });
        return Ok(result);
    }

    /// <summary>
    /// API Lấy chi tiết một Tác giả theo ID.
    /// </summary>
    [HttpGet("api/authors/{id}")]
    public async Task<IActionResult> GetAuthorByIdApi(int id, CancellationToken cancellationToken = default)
    {
        var author = await authorService.GetAuthorByIdAsync(id, cancellationToken);
        if (author == null)
        {
            return NotFound(new { message = "Không tìm thấy tác giả." });
        }

        return Ok(new
        {
            id = author.Id,
            name = author.Name,
            note = author.Note,
            status = author.Status,
            createdAtUtc = author.CreatedAtUtc
        });
    }

    /// <summary>
    /// API Thêm mới Tác giả: Nhận Name và Note, kiểm tra trùng, trả về kết quả hoặc lỗi.
    /// </summary>
    [HttpPost("api/authors")]
    public async Task<IActionResult> CreateAuthorApi(
        [FromBody] CreateAuthorDto request,
        CancellationToken cancellationToken = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new
            {
                message = "Họ tên tác giả là bắt buộc."
            });
        }

        var outcome = await authorService.CreateAuthorAsync(request.Name, request.Note, cancellationToken);
        if (!outcome.IsSuccess)
        {
            return BadRequest(new
            {
                message = outcome.ErrorMessage ?? "Tác giả với tên này đã tồn tại trong hệ thống."
            });
        }

        return StatusCode(StatusCodes.Status201Created, new
        {
            id = outcome.Author!.Id,
            name = outcome.Author.Name,
            note = outcome.Author.Note,
            status = outcome.Author.Status,
            createdAtUtc = outcome.Author.CreatedAtUtc
        });
    }

    /// <summary>
    /// API Cập nhật thông tin Tác giả: Kiểm tra ID, chống trùng tên (loại trừ chính tác giả).
    /// </summary>
    [HttpPut("api/authors/{id}")]
    public async Task<IActionResult> UpdateAuthorApi(
        int id,
        [FromBody] UpdateAuthorDto request,
        CancellationToken cancellationToken = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { message = "Họ tên tác giả là bắt buộc." });
        }

        var outcome = await authorService.UpdateAuthorAsync(id, request.Name, request.Note, cancellationToken);
        if (outcome.IsNotFound)
        {
            return NotFound(new { message = outcome.ErrorMessage ?? "Không tìm thấy thông tin tác giả cần cập nhật." });
        }

        if (!outcome.IsSuccess)
        {
            return BadRequest(new { message = outcome.ErrorMessage ?? "Tên tác giả đã tồn tại." });
        }

        return Ok(new
        {
            id = outcome.Author!.Id,
            name = outcome.Author.Name,
            note = outcome.Author.Note,
            status = outcome.Author.Status,
            createdAtUtc = outcome.Author.CreatedAtUtc
        });
    }

    /// <summary>
    /// API Chuyển trạng thái Tác giả sang "Ngừng sử dụng" hoặc trạng thái khác.
    /// </summary>
    [HttpPatch("api/authors/{id}/status")]
    public async Task<IActionResult> UpdateAuthorStatusApi(
        int id,
        [FromBody] UpdateAuthorStatusDto? request,
        CancellationToken cancellationToken = default)
    {
        AuthorStatusOutcome outcome;
        if (!string.IsNullOrWhiteSpace(request?.Status))
        {
            outcome = await authorService.SetAuthorStatusAsync(id, request.Status.Trim(), cancellationToken);
        }
        else
        {
            outcome = await authorService.ToggleAuthorStatusAsync(id, cancellationToken);
        }

        if (outcome.IsNotFound)
        {
            return NotFound(new { message = outcome.ErrorMessage ?? "Không tìm thấy thông tin tác giả." });
        }

        if (!outcome.IsSuccess)
        {
            return BadRequest(new { message = outcome.ErrorMessage ?? "Cập nhật trạng thái thất bại." });
        }

        return Ok(new
        {
            id = outcome.Author!.Id,
            name = outcome.Author.Name,
            status = outcome.Author.Status
        });
    }

    /// <summary>
    /// API Xóa Tác giả có kiểm tra ràng buộc: Chặn xóa nếu đã có sách liên kết.
    /// </summary>
    [HttpDelete("api/authors/{id}")]
    public async Task<IActionResult> DeleteAuthorApi(
        int id,
        CancellationToken cancellationToken = default)
    {
        var outcome = await authorService.DeleteAuthorAsync(id, cancellationToken);
        if (outcome.IsNotFound)
        {
            return NotFound(new { message = outcome.ErrorMessage ?? "Không tìm thấy thông tin tác giả cần xóa." });
        }

        if (outcome.HasLinkedBooks)
        {
            return BadRequest(new { message = outcome.ErrorMessage ?? "Không thể xóa tác giả này vì đã có sách liên kết." });
        }

        if (!outcome.IsSuccess)
        {
            return BadRequest(new { message = outcome.ErrorMessage ?? "Xóa tác giả thất bại." });
        }

        return Ok(new { message = "Xóa tác giả thành công." });
    }
}
