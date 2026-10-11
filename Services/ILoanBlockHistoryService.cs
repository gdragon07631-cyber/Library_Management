using Project.Models;

namespace Project.Services;

public interface ILoanBlockHistoryService
{
    Task<LoanBlockHistoryPage> SearchAsync(
        LoanBlockHistoryFilter filter, CancellationToken cancellationToken = default);

    Task<AuditBlockLog?> GetByIdAsync(long id, CancellationToken cancellationToken = default);
}
