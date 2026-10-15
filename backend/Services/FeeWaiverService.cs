using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed class FeeWaiverService(ApplicationDbContext db) : IFeeWaiverService
{
    public async Task<IReadOnlyList<FeeWaiverReceivableViewModel>> GetEligibleReceivablesAsync(CancellationToken cancellationToken = default)
    {
        var fees = await db.ReaderFees.AsNoTracking()
            .Include(fee => fee.ReaderAccount)
            .Include(fee => fee.BookLoan).ThenInclude(loan => loan!.Book)
            .Include(fee => fee.BookLoan).ThenInclude(loan => loan!.BookCopy)
            .Where(fee => fee.ReaderAccount != null && fee.ReaderAccount.OutstandingBalance > 0m &&
                !fee.IsPaid)
            .OrderBy(fee => fee.CreatedAtUtc).ThenBy(fee => fee.Id)
            .ToListAsync(cancellationToken);

        return fees.Where(fee => Remaining(fee) > 0m).Select(fee => new FeeWaiverReceivableViewModel(
            fee.Id, fee.ReaderAccount!.FullName, fee.ReaderAccount.StudentOrStaffCode,
            fee.BookLoanId, fee.BookLoan?.Book?.Title, fee.BookLoan?.BookCopy?.CopyCode,
            fee.Amount, fee.PaidAmount, fee.WaivedAmount, Remaining(fee),
            fee.ReaderAccount.OutstandingBalance, fee.CreatedAtUtc)).ToList();
    }

    public async Task<IReadOnlyList<FeeWaiverHistoryViewModel>> GetRequestsByLibrarianAsync(
        int adminAccountId, CancellationToken cancellationToken = default)
    {
        var requests = await RequestQuery()
            .Where(request => request.RequestedByAdminAccountId == adminAccountId)
            .OrderByDescending(request => request.RequestedAtUtc).ThenByDescending(request => request.Id)
            .ToListAsync(cancellationToken);
        return requests.Select(ToViewModel).ToList();
    }

    public async Task<IReadOnlyList<FeeWaiverHistoryViewModel>> GetPendingRequestsAsync(CancellationToken cancellationToken = default)
    {
        var requests = await RequestQuery()
            .Where(request => request.Status == FeeWaiverRequestStatus.Pending)
            .OrderBy(request => request.RequestedAtUtc).ThenBy(request => request.Id)
            .ToListAsync(cancellationToken);
        return requests.Select(ToViewModel).ToList();
    }

    public async Task<FeeWaiverHistoryViewModel?> GetRequestAsync(long requestId, CancellationToken cancellationToken = default)
    {
        var request = await RequestQuery().SingleOrDefaultAsync(item => item.Id == requestId, cancellationToken);
        return request is null ? null : ToViewModel(request);
    }

    public async Task<FeeWaiverOperationResult> CreateRequestAsync(
        int readerFeeId, decimal requestedAmount, string? reason, int requestedByAdminAccountId,
        CancellationToken cancellationToken = default)
    {
        var normalizedReason = reason?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedReason))
            return new(false, "Vui lòng nhập lý do đề nghị miễn giảm.");
        if (normalizedReason.Length > 1000)
            return new(false, "Lý do không được vượt quá 1.000 ký tự.");
        if (requestedAmount <= 0m || decimal.Round(requestedAmount, 2) != requestedAmount)
            return new(false, "Số tiền đề nghị phải lớn hơn 0 và có tối đa 2 chữ số thập phân.");

        await using var transaction = await BeginTransactionIfRelationalAsync(cancellationToken, IsolationLevel.Serializable);
        var fee = await db.ReaderFees.Include(item => item.ReaderAccount)
            .SingleOrDefaultAsync(item => item.Id == readerFeeId, cancellationToken);
        if (fee is null || fee.ReaderAccount is null)
            return new(false, "Không tìm thấy khoản phải thu.");
        var remaining = Remaining(fee);
        if (remaining <= 0m || fee.ReaderAccount.OutstandingBalance <= 0m)
            return new(false, "Khoản phải thu không còn số dư để đề nghị miễn giảm.");
        if (requestedAmount > remaining || requestedAmount > fee.ReaderAccount.OutstandingBalance)
            return new(false, "Số tiền đề nghị không được vượt quá số dư còn phải thu.");

        db.FeeWaiverRequests.Add(new FeeWaiverRequest
        {
            ReaderFeeId = fee.Id,
            OriginalFeeAmount = fee.Amount,
            FeeBalanceAtRequest = remaining,
            ReaderBalanceAtRequest = fee.ReaderAccount.OutstandingBalance,
            RequestedAmount = requestedAmount,
            Reason = normalizedReason,
            Status = FeeWaiverRequestStatus.Pending,
            RequestedByAdminAccountId = requestedByAdminAccountId,
            RequestedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new(true);
    }

    public async Task<FeeWaiverOperationResult> DecideAsync(
        long requestId, bool approve, string? reviewNote, int reviewedByAdminAccountId,
        CancellationToken cancellationToken = default)
    {
        var normalizedNote = string.IsNullOrWhiteSpace(reviewNote) ? null : reviewNote.Trim();
        if (normalizedNote?.Length > 1000)
            return new(false, "Ghi chú xử lý không được vượt quá 1.000 ký tự.");

        await using var transaction = await BeginTransactionIfRelationalAsync(cancellationToken, IsolationLevel.ReadCommitted);
        var request = await db.FeeWaiverRequests
            .Include(item => item.ReaderFee).ThenInclude(fee => fee!.ReaderAccount)
            .SingleOrDefaultAsync(item => item.Id == requestId, cancellationToken);
        if (request is null) return new(false, "Không tìm thấy đề nghị miễn giảm.");
        if (request.Status != FeeWaiverRequestStatus.Pending)
            return new(false, "Đề nghị này đã được xử lý.", IsConflict: true);

        var now = DateTime.UtcNow;
        var nextStatus = approve ? FeeWaiverRequestStatus.Approved : FeeWaiverRequestStatus.Rejected;
        if (approve && (request.ReaderFee is null || request.ReaderFee.ReaderAccount is null ||
            Remaining(request.ReaderFee) < request.RequestedAmount ||
            request.ReaderFee.ReaderAccount.OutstandingBalance < request.RequestedAmount))
            return new(false, "Số dư khoản phải thu đã thay đổi; đề nghị không thể được duyệt với số tiền này.", IsConflict: true);

        if (!db.Database.IsRelational())
        {
            if (approve)
            {
                var memoryFee = request.ReaderFee!;
                memoryFee.WaivedAmount += request.RequestedAmount;
                memoryFee.ReaderAccount!.OutstandingBalance -= request.RequestedAmount;
            }
            request.Status = nextStatus;
            request.ReviewedByAdminAccountId = reviewedByAdminAccountId;
            request.ReviewedAtUtc = now;
            request.ReviewNote = normalizedNote;
            await db.SaveChangesAsync(cancellationToken);
            return new(true);
        }

        var claimed = await db.FeeWaiverRequests
            .Where(item => item.Id == requestId && item.Status == FeeWaiverRequestStatus.Pending)
            .ExecuteUpdateAsync(update => update.SetProperty(
                item => item.Status, FeeWaiverRequestStatus.Processing), cancellationToken);
        if (claimed != 1)
        {
            await RollbackIfNeededAsync(transaction, cancellationToken);
            return new(false, "Đề nghị này vừa được xử lý bởi tài khoản khác.", IsConflict: true);
        }

        if (approve)
        {
            var reader = request.ReaderFee!.ReaderAccount!;

            if (db.Database.IsRelational())
            {
                var feeUpdated = await db.ReaderFees
                    .Where(item => item.Id == request.ReaderFeeId && item.Amount - item.PaidAmount - item.WaivedAmount >= request.RequestedAmount)
                    .ExecuteUpdateAsync(update => update.SetProperty(
                        item => item.WaivedAmount, item => item.WaivedAmount + request.RequestedAmount), cancellationToken);
                if (feeUpdated != 1)
                {
                    await RollbackIfNeededAsync(transaction, cancellationToken);
                    return new(false, "Số dư khoản phải thu đã thay đổi; đề nghị không thể được duyệt.", IsConflict: true);
                }

                var balanceUpdated = await db.ReaderAccounts
                    .Where(item => item.Id == reader.Id && item.OutstandingBalance >= request.RequestedAmount)
                    .ExecuteUpdateAsync(update => update.SetProperty(
                        item => item.OutstandingBalance, item => item.OutstandingBalance - request.RequestedAmount), cancellationToken);
                if (balanceUpdated != 1)
                {
                    await RollbackIfNeededAsync(transaction, cancellationToken);
                    return new(false, "Số dư bạn đọc đã thay đổi; đề nghị không thể được duyệt.", IsConflict: true);
                }

                var requestUpdated = await db.FeeWaiverRequests
                    .Where(item => item.Id == requestId && item.Status == FeeWaiverRequestStatus.Processing)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(item => item.Status, nextStatus)
                        .SetProperty(item => item.ReviewedByAdminAccountId, (int?)reviewedByAdminAccountId)
                        .SetProperty(item => item.ReviewedAtUtc, (DateTime?)now)
                        .SetProperty(item => item.ReviewNote, normalizedNote), cancellationToken);
                if (requestUpdated != 1)
                {
                    await RollbackIfNeededAsync(transaction, cancellationToken);
                    return new(false, "Đề nghị này vừa được xử lý bởi tài khoản khác.", IsConflict: true);
                }
            }
        }
        else
        {
            var requestUpdated = await db.FeeWaiverRequests
                .Where(item => item.Id == requestId && item.Status == FeeWaiverRequestStatus.Processing)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.Status, nextStatus)
                    .SetProperty(item => item.ReviewedByAdminAccountId, (int?)reviewedByAdminAccountId)
                    .SetProperty(item => item.ReviewedAtUtc, (DateTime?)now)
                    .SetProperty(item => item.ReviewNote, normalizedNote), cancellationToken);
            if (requestUpdated != 1)
            {
                await RollbackIfNeededAsync(transaction, cancellationToken);
                return new(false, "Đề nghị này vừa được xử lý bởi tài khoản khác.", IsConflict: true);
            }
        }

        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new(true);
    }

    private IQueryable<FeeWaiverRequest> RequestQuery() => db.FeeWaiverRequests.AsNoTracking()
        .Include(request => request.ReaderFee).ThenInclude(fee => fee!.ReaderAccount)
        .Include(request => request.ReaderFee).ThenInclude(fee => fee!.BookLoan).ThenInclude(loan => loan!.Book)
        .Include(request => request.ReaderFee).ThenInclude(fee => fee!.BookLoan).ThenInclude(loan => loan!.BookCopy)
        .Include(request => request.RequestedByAdminAccount)
        .Include(request => request.ReviewedByAdminAccount);

    private static decimal Remaining(ReaderFee fee) => fee.RemainingAmount;

    private static FeeWaiverHistoryViewModel ToViewModel(FeeWaiverRequest request)
    {
        var fee = request.ReaderFee!;
        var reader = fee.ReaderAccount!;
        return new(request.Id, fee.Id, fee.BookLoanId, reader.FullName, reader.StudentOrStaffCode,
            fee.BookLoan?.Book?.Title, fee.BookLoan?.BookCopy?.CopyCode,
            request.OriginalFeeAmount, request.FeeBalanceAtRequest, request.ReaderBalanceAtRequest,
            request.RequestedAmount, fee.PaidAmount, fee.WaivedAmount, Remaining(fee),
            reader.OutstandingBalance, request.Reason, request.Status,
            request.RequestedByAdminAccountId,
            request.RequestedByAdminAccount?.FullName ?? request.RequestedByAdminAccount?.Email ?? $"#{request.RequestedByAdminAccountId}",
            request.RequestedAtUtc, request.ReviewedByAdminAccountId,
            request.ReviewedByAdminAccount?.FullName ?? request.ReviewedByAdminAccount?.Email,
            request.ReviewedAtUtc, request.ReviewNote);
    }

    private Task<IDbContextTransaction?> BeginTransactionIfRelationalAsync(CancellationToken cancellationToken, IsolationLevel isolationLevel) =>
        db.Database.IsRelational()
            ? BeginTransactionAsync(isolationLevel, cancellationToken)
            : Task.FromResult<IDbContextTransaction?>(null);

    private async Task<IDbContextTransaction?> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken) =>
        await db.Database.BeginTransactionAsync(isolationLevel, cancellationToken);

    private static async Task RollbackIfNeededAsync(IDbContextTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
    }
}
