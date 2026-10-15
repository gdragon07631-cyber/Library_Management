using Project.Models;

namespace Project.Services;

public sealed record FeeWaiverOperationResult(
    bool IsSuccess, string? ErrorMessage = null, bool IsConflict = false);

public interface IFeeWaiverService
{
    Task<IReadOnlyList<FeeWaiverReceivableViewModel>> GetEligibleReceivablesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FeeWaiverHistoryViewModel>> GetRequestsByLibrarianAsync(int adminAccountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FeeWaiverHistoryViewModel>> GetPendingRequestsAsync(CancellationToken cancellationToken = default);
    Task<FeeWaiverHistoryViewModel?> GetRequestAsync(long requestId, CancellationToken cancellationToken = default);
    Task<FeeWaiverOperationResult> CreateRequestAsync(int readerFeeId, decimal requestedAmount, string? reason,
        int requestedByAdminAccountId, CancellationToken cancellationToken = default);
    Task<FeeWaiverOperationResult> DecideAsync(long requestId, bool approve, string? reviewNote,
        int reviewedByAdminAccountId, CancellationToken cancellationToken = default);
}
