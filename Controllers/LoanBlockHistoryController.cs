using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Project.Filters;
using Project.Models;
using Project.Services;

namespace Project.Controllers;

[StaffOnly(AccountRoles.LibraryManager)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LoanBlockHistoryController(
    ILoanBlockHistoryService history, ILogger<LoanBlockHistoryController> logger) : Controller
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(
        [FromQuery] LoanBlockHistoryFilter filter, long? selectedId = null,
        CancellationToken cancellationToken = default)
    {
        var model = new LoanBlockHistoryIndexViewModel { Filter = filter };
        if (!ModelState.IsValid)
        {
            model.ErrorMessage = "Điều kiện lọc không hợp lệ. Vui lòng kiểm tra lại.";
            return View(model);
        }
        if (filter.FromDate > filter.ToDate)
        {
            model.ErrorMessage = "Từ ngày không được lớn hơn đến ngày.";
            return View(model);
        }
        if (!string.IsNullOrWhiteSpace(filter.EventType) &&
            LoanBlockHistoryEventTypes.Options.All(option => option.Value != filter.EventType))
        {
            model.ErrorMessage = "Loại sự kiện không hợp lệ.";
            return View(model);
        }

        try
        {
            var result = await history.SearchAsync(filter, cancellationToken);
            model.Records = result.Records;
            model.TotalCount = result.TotalCount;
            model.PageCount = result.PageCount;

            if (selectedId is { } id)
            {
                model.SelectedRecord = await history.GetByIdAsync(id, cancellationToken);
                if (model.SelectedRecord is null) model.ErrorMessage = "Không tìm thấy bản ghi lịch sử đã chọn.";
            }
        }
        catch (DbException exception)
        {
            logger.LogError(exception, "Không thể truy vấn lịch sử chặn và bỏ qua chặn.");
            model.ErrorMessage = "Không tải được nhật ký lúc này. Vui lòng thử lại sau.";
        }

        return View(model);
    }

    [HttpGet("api/loan-block-history")]
    public async Task<IActionResult> SearchApi(
        [FromQuery] LoanBlockHistoryFilter filter, CancellationToken cancellationToken = default)
    {
        if (!TryValidateModel(filter))
            return BadRequest(new { message = "Điều kiện lọc không hợp lệ." });
        if (filter.FromDate > filter.ToDate)
            return BadRequest(new { message = "Từ ngày không được lớn hơn đến ngày." });
        if (!string.IsNullOrWhiteSpace(filter.EventType) &&
            LoanBlockHistoryEventTypes.Options.All(option => option.Value != filter.EventType))
            return BadRequest(new { message = "Loại sự kiện không hợp lệ." });

        try
        {
            var result = await history.SearchAsync(filter, cancellationToken);
            return Ok(new
            {
                result.TotalCount,
                result.Page,
                result.PageSize,
                result.PageCount,
                items = result.Records.Select(ToApiModel)
            });
        }
        catch (DbException exception)
        {
            logger.LogError(exception, "API không thể truy vấn lịch sử chặn và bỏ qua chặn.");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "Không thể truy vấn lịch sử lúc này." });
        }
    }

    [HttpGet("api/loan-block-history/{id:long}")]
    public async Task<IActionResult> DetailsApi(long id, CancellationToken cancellationToken = default)
    {
        try
        {
            var record = await history.GetByIdAsync(id, cancellationToken);
            return record is null ? NotFound(new { message = "Không tìm thấy bản ghi lịch sử." }) : Ok(ToApiModel(record));
        }
        catch (DbException exception)
        {
            logger.LogError(exception, "API không thể tải chi tiết bản ghi lịch sử {HistoryId}.", id);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "Không thể tải chi tiết lịch sử lúc này." });
        }
    }

    private static object ToApiModel(AuditBlockLog record) => new
    {
        record.Id,
        record.ReaderAccountId,
        record.ReaderCode,
        record.ReaderName,
        record.OccurredAtUtc,
        record.Actor,
        record.ActorRole,
        record.EventType,
        eventLabel = LoanBlockHistoryEventTypes.Label(record.EventType),
        outcomeLabel = LoanBlockHistoryEventTypes.OutcomeLabel(record.Outcome),
        blockingReasonCodes = string.IsNullOrWhiteSpace(record.BlockingReasonCodes)
            ? Array.Empty<string>()
            : record.BlockingReasonCodes.Split('|', StringSplitOptions.RemoveEmptyEntries),
        reasons = record.Reasons.Split("; ", StringSplitOptions.RemoveEmptyEntries),
        record.BorrowedCount,
        record.BorrowingLimit,
        record.CardStatus,
        record.CardExpiresOn,
        record.OverdueLoanCount,
        record.OutstandingBalance,
        outstandingBalanceFormatted = ReaderBorrowingStatus.FormatVnd(record.OutstandingBalance),
        record.OverrideReason,
        record.OverrideRequestId,
        record.Outcome,
        record.FailureReason,
        record.BookLoanId,
        record.IpAddress
    };
}
