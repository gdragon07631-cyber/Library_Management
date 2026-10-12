namespace Project.Services;

public sealed record HoldPickupConfirmationResult(bool IsSuccess, string Message, long? LoanId = null);

public interface IHoldPickupConfirmationService
{
    Task<HoldPickupConfirmationResult> ConfirmAsync(long holdId, string libraryCardCode, string actor, int? createdByAdminAccountId, CancellationToken cancellationToken = default);
}
