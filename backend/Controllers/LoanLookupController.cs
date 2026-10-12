using Microsoft.AspNetCore.Mvc;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

/// <summary>Tra nhanh phiếu mượn (chỉ đọc) cho thủ thư; không thay đổi màn hình hay quyền của LoanController.</summary>
[StaffOnly(AccountRoles.Librarian, AccountRoles.SystemAdmin, AccountRoles.LibraryManager)]
public sealed class LoanLookupController(ILoanLookupService lookup, ILogger<LoanLookupController> logger) : Controller
{
    public const string EmptyCodeMessage = "Vui lòng nhập mã thẻ, mã vạch bản sao hoặc mã phiếu mượn.";
    public const string SystemErrorMessage = "Đã xảy ra lỗi hệ thống khi tra cứu phiếu mượn. Vui lòng thử lại.";

    /// <summary>Giá trị tham số <c>op</c> do các nút của form tra cứu gửi lên.</summary>
    public const string SearchOperation = "search";
    public const string ApplyFilterOperation = "apply";
    public const string ClearFilterOperation = "clear";

    /// <param name="q">Mã tra cứu.</param>
    /// <param name="page">Trang kết quả.</param>
    /// <param name="from">Từ ngày (yyyy-MM-dd); với <paramref name="op"/> = apply là giá trị trong ô lọc.</param>
    /// <param name="to">Đến ngày (yyyy-MM-dd).</param>
    /// <param name="status">Trạng thái; rỗng = tất cả.</param>
    /// <param name="op">Nút đã bấm: search (tìm mã, giữ bộ lọc đang áp dụng), apply hoặc clear; rỗng = URL chuẩn.</param>
    /// <param name="appliedFrom">Bộ lọc đang áp dụng, nút Tìm kiếm gửi kèm để giữ bộ lọc khi tìm mã mới.</param>
    /// <param name="appliedTo">Như <paramref name="appliedFrom"/>.</param>
    /// <param name="appliedStatus">Như <paramref name="appliedFrom"/>.</param>
    /// <param name="ct">Token hủy.</param>
    [HttpGet]
    public async Task<IActionResult> Index(string? q, int page = 1, string? from = null, string? to = null,
        string? status = null, string? op = null, string? appliedFrom = null, string? appliedTo = null,
        string? appliedStatus = null, CancellationToken ct = default)
    {
        var code = q?.Trim();
        switch (op)
        {
            case ClearFilterOperation:
                // Xóa hai ngày, về "Tất cả trạng thái", giữ mã tìm và tải lại trang 1.
                if (string.IsNullOrEmpty(code)) return View(new LoanLookupViewModel { Code = code, ValidationMessage = EmptyCodeMessage });
                return RedirectToAction(nameof(Index), new { q = code });

            case ApplyFilterOperation:
            {
                var filter = LoanLookupFilter.TryParse(from, to, status, out var error);
                if (filter is null)
                {
                    // Khoảng ngày sai: báo lỗi, giữ dữ liệu nhập và không truy vấn.
                    return View(new LoanLookupViewModel
                    {
                        Code = code, From = from, To = to, Status = status, FilterError = error,
                        AppliedFrom = appliedFrom, AppliedTo = appliedTo, AppliedStatus = appliedStatus
                    });
                }
                if (string.IsNullOrEmpty(code)) return View(EmptyCodeModel(code, filter));
                return RedirectToCanonical(code, filter);
            }

            case SearchOperation:
            {
                // Tìm mã mới dùng bộ lọc đang áp dụng (không lấy giá trị đã sửa nhưng chưa áp dụng) và về trang 1.
                var filter = LoanLookupFilter.TryParse(appliedFrom, appliedTo, appliedStatus, out _) ?? LoanLookupFilter.None;
                if (string.IsNullOrEmpty(code)) return View(EmptyCodeModel(code, filter));
                return RedirectToCanonical(code, filter);
            }
        }

        var model = new LoanLookupViewModel { Code = code, From = from, To = to, Status = status };
        var appliedFilter = LoanLookupFilter.TryParse(from, to, status, out var filterError);
        if (appliedFilter is null)
        {
            model.FilterError = filterError;
            return View(model);
        }
        SetApplied(model, appliedFilter);

        // Mở màn hình lần đầu (không có tham số q) chưa phải là một lần tìm kiếm.
        if (q is null && appliedFilter.IsEmpty) return View(model);
        // Chỉ nhập bộ lọc mà không có mã: giữ quy tắc Lát 1, không trả toàn bộ phiếu.
        if (string.IsNullOrEmpty(code))
        {
            model.ValidationMessage = EmptyCodeMessage;
            return View(model);
        }

        try
        {
            model.Result = await lookup.SearchAsync(code, page, appliedFilter, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Tra nhanh phiếu mượn thất bại.");
            model.ErrorMessage = SystemErrorMessage;
        }
        return View(model);
    }

    [HttpGet("api/loan-lookup")]
    public async Task<IActionResult> SearchApi([FromQuery] string? code, [FromQuery] int page = 1,
        [FromQuery] string? from = null, [FromQuery] string? to = null, [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        var filter = LoanLookupFilter.TryParse(from, to, status, out var filterError);
        if (filter is null) return BadRequest(new { message = filterError });
        var result = await lookup.SearchAsync(code, page, filter, ct);
        if (result is null) return BadRequest(new { message = EmptyCodeMessage });
        return Ok(new
        {
            code = result.Code,
            items = result.Items.Select(item => new
            {
                loanId = item.LoanId, cardCode = item.CardCode, copyCode = item.CopyCode, readerName = item.ReaderName,
                loanDate = item.LoanDate, dueDate = item.DueDate, status = item.Status
            }),
            totalItems = result.TotalItems,
            page = result.Page,
            pageSize = result.PageSize,
            totalPages = result.TotalPages,
            from = result.Filter.FromText,
            to = result.Filter.ToText,
            status = result.Filter.Status,
            emptyReason = LoanLookupEmptyReasonCodes.ToApiValue(result.EmptyReason)
        });
    }

    private RedirectToActionResult RedirectToCanonical(string code, LoanLookupFilter filter) =>
        RedirectToAction(nameof(Index), new { q = code, from = filter.FromText, to = filter.ToText, status = filter.Status });

    private static LoanLookupViewModel EmptyCodeModel(string? code, LoanLookupFilter filter)
    {
        var model = new LoanLookupViewModel { Code = code, ValidationMessage = EmptyCodeMessage };
        model.From = filter.FromText;
        model.To = filter.ToText;
        model.Status = filter.Status;
        SetApplied(model, filter);
        return model;
    }

    private static void SetApplied(LoanLookupViewModel model, LoanLookupFilter filter)
    {
        model.AppliedFrom = filter.FromText;
        model.AppliedTo = filter.ToText;
        model.AppliedStatus = filter.Status;
    }
}
