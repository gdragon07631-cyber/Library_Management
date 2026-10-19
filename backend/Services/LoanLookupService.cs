using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

/// <summary>
/// Tra nhanh phiếu mượn bằng một ô tìm kiếm. Mã được bỏ khoảng trắng đầu/cuối và so khớp chính xác với cả ba loại
/// mã cùng lúc (không đoán loại theo độ dài/tiền tố); một phiếu khớp nhiều điều kiện chỉ xuất hiện một lần vì
/// mọi điều kiện được gộp bằng OR trên cùng bảng BookLoans.
/// </summary>
public sealed class LoanLookupService(ApplicationDbContext db) : ILoanLookupService
{
    public const int PageSize = 20;

    public Task<LoanLookupPage?> SearchAsync(string? code, int page, CancellationToken cancellationToken = default) =>
        SearchAsync(code, page, LoanLookupFilter.None, cancellationToken);

    public async Task<LoanLookupPage?> SearchAsync(
        string? code, int page, LoanLookupFilter filter, CancellationToken cancellationToken = default)
    {
        var filterError = filter.Validate();
        if (filterError is not null) throw new ArgumentException(filterError, nameof(filter));

        var term = code?.Trim();
        if (string.IsNullOrEmpty(term)) return null;

        // Mã phiếu là khóa chính BookLoan.Id; nhận cả dạng "#123" như cách nhật ký hoạt động hiển thị.
        var loanIdText = term.StartsWith('#') ? term[1..] : term;
        long? loanId = long.TryParse(loanIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : null;

        var query = db.BookLoans.AsNoTracking().Where(loan =>
            (loanId != null && loan.Id == loanId) ||
            db.LibraryCards.Any(card => card.ReaderAccountId == loan.ReaderAccountId && card.CardCode == term) ||
            db.BookCopies.Any(copy => copy.Id == loan.BookCopyId && copy.CopyCode == term));
        var codeOnlyQuery = query;

        // Bộ lọc nối bằng AND sau điều kiện mã nên áp dụng cho mọi nhánh khớp mã. LoanDate là cột date (không có
        // giờ, không lưu UTC) nên "≥ đầu ngày Từ" và "< đầu ngày sau Đến" chính là From ≤ LoanDate ≤ To.
        if (filter.From is DateOnly from) query = query.Where(loan => loan.LoanDate >= from);
        if (filter.To is DateOnly to) query = query.Where(loan => loan.LoanDate <= to);
        if (filter.Status == LoanLookupStatus.Open) query = query.Where(loan => loan.ReturnedAtUtc == null);
        if (filter.Status == LoanLookupStatus.Returned) query = query.Where(loan => loan.ReturnedAtUtc != null);

        var total = await query.CountAsync(cancellationToken);
        var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)PageSize);
        page = totalPages == 0 ? 1 : Math.Clamp(page, 1, totalPages);

        var rawItems = await query
            .OrderBy(loan => loan.ReturnedAtUtc != null)
            .ThenByDescending(loan => loan.LoanDate).ThenByDescending(loan => loan.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(loan => new LoanLookupItem(
                loan.Id,
                db.LibraryCards.Where(card => card.ReaderAccountId == loan.ReaderAccountId)
                    .Select(card => card.CardCode).FirstOrDefault(),
                loan.BookCopy == null ? null : loan.BookCopy.CopyCode,
                loan.ReaderAccount!.FullName,
                loan.LoanDate,
                loan.DueDate,
                loan.ReturnedAtUtc == null ? LoanLookupStatus.Open : LoanLookupStatus.Returned)
            {
                ReaderAccountId = loan.ReaderAccountId
            })
            .ToListAsync(cancellationToken);

        var readerIds = rawItems.Select(item => item.ReaderAccountId).Where(id => id > 0).Distinct().ToList();
        var evaluations = new Dictionary<int, ReaderLateReturnEvaluation>();
        if (readerIds.Count > 0)
        {
            var today = HoldPickupExpirySchedule.GetBusinessDate(DateTime.UtcNow);
            var readerLoans = await db.BookLoans.AsNoTracking()
                .Where(loan => readerIds.Contains(loan.ReaderAccountId))
                .ToListAsync(cancellationToken);
            foreach (var group in readerLoans.GroupBy(loan => loan.ReaderAccountId))
                evaluations[group.Key] = ReaderLoanHistoryViewModel.Evaluate(group.ToList(), today);
        }

        var items = rawItems.Select(item =>
        {
            if (evaluations.TryGetValue(item.ReaderAccountId, out var eval))
                return item with { ReaderLateReturnCount = eval.LateReturnCount, IsFrequentLateReturner = eval.IsFrequentLateReturner };
            return item;
        }).ToList();

        int? matchedReaderId = readerIds.Count == 1 ? readerIds[0] : null;
        string? matchedReaderName = matchedReaderId.HasValue ? items.FirstOrDefault()?.ReaderName : null;
        var matchedEval = matchedReaderId.HasValue && evaluations.TryGetValue(matchedReaderId.Value, out var singleEval)
            ? singleEval
            : null;

        return new LoanLookupPage(term, items, page, PageSize, total)
        {
            Filter = filter,
            EmptyReason = total > 0 ? null : await FindEmptyReasonAsync(codeOnlyQuery, filter, cancellationToken),
            MatchedReaderId = matchedReaderId,
            MatchedReaderName = matchedReaderName,
            IsFrequentLateReturner = matchedEval?.IsFrequentLateReturner ?? false,
            FrequentLateWarningMessage = matchedEval?.WarningMessage
        };
    }

    /// <summary>
    /// Chỉ chạy khi không có kết quả. Không có bộ lọc thì chắc chắn là mã không khớp (không cần truy vấn thêm); có bộ
    /// lọc thì kiểm tra tồn tại trên cùng điều kiện mã (cả ba loại mã), chỉ bỏ khoảng ngày và trạng thái.
    /// </summary>
    private static async Task<LoanLookupEmptyReason> FindEmptyReasonAsync(
        IQueryable<BookLoan> codeOnlyQuery, LoanLookupFilter filter, CancellationToken cancellationToken)
    {
        if (filter.IsEmpty) return LoanLookupEmptyReason.NoMatchingCode;
        return await codeOnlyQuery.AnyAsync(cancellationToken)
            ? LoanLookupEmptyReason.FilteredOut
            : LoanLookupEmptyReason.NoMatchingCode;
    }
}
