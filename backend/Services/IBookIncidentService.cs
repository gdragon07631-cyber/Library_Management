using Project.Models;

namespace Project.Services;

public interface IBookIncidentService
{
    Task<IReadOnlyList<OpenLoanItemViewModel>> GetOpenLoansAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BookCopyIncidentItemViewModel>> GetCopiesForLoanAsync(long loanId, CancellationToken cancellationToken = default);
    Task<RecordIncidentOutcome> RecordIncidentAsync(RecordIncidentRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BookIncident>> GetRecentIncidentsAsync(int count = 20, CancellationToken cancellationToken = default);
}

