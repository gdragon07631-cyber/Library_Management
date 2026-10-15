using Project.Models;

namespace Project.Services;

public sealed class CompensationPolicyOutcome
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
    public CompensationPolicy? Policy { get; init; }

    public static CompensationPolicyOutcome Success(CompensationPolicy policy) =>
        new() { IsSuccess = true, Policy = policy };

    public static CompensationPolicyOutcome Failed(string message) =>
        new() { IsSuccess = false, ErrorMessage = message };
}

public interface ICompensationService
{
    Task<CompensationPolicy?> GetPolicyAsync(CancellationToken cancellationToken = default);
    Task<CompensationPolicyOutcome> UpdatePolicyAsync(UpdateCompensationPolicyRequest request, string staffRole, string staffEmail, CancellationToken cancellationToken = default);
    Task<CompensationOutcome> CalculateAndCreateCompensationAsync(long incidentId, CancellationToken cancellationToken = default);
    Task<BookIncidentCompensation?> GetCompensationByIncidentIdAsync(long incidentId, CancellationToken cancellationToken = default);
    Task<CompensationOutcome> AdjustCompensationAsync(AdjustCompensationRequest request, string staffRole, string staffEmail, CancellationToken cancellationToken = default);
}

