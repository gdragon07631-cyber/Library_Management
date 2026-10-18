using Project.Models;

namespace Project.Services;

public interface IFeePaymentService
{
    Task<IReadOnlyList<FeePaymentReader>> SearchAsync(string? search, CancellationToken cancellationToken = default);
    Task<FeePaymentDebt?> GetDebtAsync(int readerId, CancellationToken cancellationToken = default);
    Task<FeePaymentResult> CollectAsync(CollectFeeViewModel request, int staffId, CancellationToken cancellationToken = default);
}
