using Project.Models;

namespace Project.Services;

public sealed class ReaderRegistrationOutcome
{
    public bool IsSuccess { get; }
    public bool IsEmailDuplicate { get; }
    public bool IsCodeDuplicate { get; }
    public ReaderAccount? Account { get; }

    public ReaderRegistrationOutcome(bool isSuccess, bool isEmailDuplicate, bool isCodeDuplicate, ReaderAccount? account = null)
    {
        IsSuccess = isSuccess;
        IsEmailDuplicate = isEmailDuplicate;
        IsCodeDuplicate = isCodeDuplicate;
        Account = account;
    }

    public static ReaderRegistrationOutcome Success(ReaderAccount account) =>
        new(true, false, false, account);

    public static ReaderRegistrationOutcome Duplicate(bool emailDuplicate, bool codeDuplicate) =>
        new(false, emailDuplicate, codeDuplicate, null);
}

public enum ReaderContactUpdateResult
{
    Success,
    NotFound,
    InvalidCurrentPassword,
    /// <summary>Đã lưu email mới ở trạng thái chờ; email chỉ đổi khi bạn đọc xác nhận qua liên kết gửi tới địa chỉ mới.</summary>
    EmailChangePending,
    /// <summary>Email mới đã được tài khoản khác sử dụng.</summary>
    EmailInUse
}

public enum ReaderPasswordChangeResult
{
    Success,
    NotFound,
    IncorrectCurrentPassword,
    PasswordRecentlyUsed
}

public sealed class DocumentHoldOutcome
{
    public bool IsAllowed { get; }
    public string Message { get; }
    public int? QueuePosition { get; }
    public bool IsReserved { get; }
    public string? CopyCode { get; }
    public DateTime? PickupDeadlineUtc { get; }

    private DocumentHoldOutcome(bool isAllowed, string message, int? queuePosition = null,
        bool isReserved = false, string? copyCode = null, DateTime? pickupDeadlineUtc = null)
    {
        IsAllowed = isAllowed;
        Message = message;
        QueuePosition = queuePosition;
        IsReserved = isReserved;
        CopyCode = copyCode;
        PickupDeadlineUtc = pickupDeadlineUtc;
    }

    public static DocumentHoldOutcome Success(int? queuePosition, BookHoldFulfillmentResult fulfillment)
    {
        if (fulfillment.IsAssigned)
        {
            var deadline = HoldPickupExpirySchedule.GetVietnamLocalTime(fulfillment.PickupDeadlineUtc!.Value)
                .ToString("dd/MM/yyyy HH:mm");
            var message = $"Đặt giữ thành công. Trạng thái yêu cầu: {BookHoldStatus.Available}; trạng thái bản sao: {BookCopyStatus.OnHold}. Mã bản sao: {fulfillment.CopyCode}. Hạn đến nhận: {deadline}.";
            return new(true, message, queuePosition, true, fulfillment.CopyCode, fulfillment.PickupDeadlineUtc);
        }

        var waitingMessage = $"Đặt giữ thành công. Trạng thái: {BookHoldStatus.Waiting}. Vị trí hiện tại trong hàng đợi: {queuePosition}. Khi có sách trả về hoặc bản sao sẵn sàng, hệ thống sẽ tự động giữ sách để bạn đến nhận.";
        return new(true, waitingMessage, queuePosition);
    }
    public static DocumentHoldOutcome Rejected(string message) => new(false, message);
    public static DocumentHoldOutcome Failed(string message) => new(false, message);
}

public sealed record ReaderBookHoldItem(
    long Id, int BookId, string BookTitle, DateTime HeldAtUtc, string Status,
    int? QueuePosition = null, DateTime? PickupDeadlineUtc = null, string? CopyBarcode = null)
{
    public bool CanCancel => Status == BookHoldStatus.Waiting || BookHoldStatus.WaitingPickupStatuses.Contains(Status);

    /// <summary>Hạn nhận sách theo giờ địa phương, dạng "17:00 28/09/2026"; null khi không có hạn.</summary>
    public string? PickupDeadlineText => PickupDeadlineUtc is { } deadline
        ? HoldPickupExpirySchedule.GetVietnamLocalTime(deadline).ToString("HH:mm dd/MM/yyyy")
        : null;
}

public enum BookHoldCancelResult
{
    Success,
    NotFound,
    NotWaiting
}

public sealed class BookHoldCancelOutcome
{
    public BookHoldCancelResult Result { get; }
    public string Message { get; }
    public string? Status { get; }
    /// <summary>Đơn của người kế tiếp được đôn lên "Đã có sách" (nếu có).</summary>
    public long? PromotedHoldId { get; private init; }
    /// <summary>Bản sao được trả về "Sẵn sàng" (nếu có).</summary>
    public long? ReleasedCopyId { get; private init; }

    private BookHoldCancelOutcome(BookHoldCancelResult result, string message, string? status)
        => (Result, Message, Status) = (result, message, status);

    public static BookHoldCancelOutcome Success(long? promotedHoldId = null, long? releasedCopyId = null) =>
        new(BookHoldCancelResult.Success, "Hủy đơn đặt giữ thành công.", BookHoldStatus.Cancelled)
        {
            PromotedHoldId = promotedHoldId,
            ReleasedCopyId = releasedCopyId
        };
    public static BookHoldCancelOutcome NotFound() =>
        new(BookHoldCancelResult.NotFound, "Không tìm thấy đơn đặt giữ.", null);
    public static BookHoldCancelOutcome NotWaiting(string status) =>
        new(BookHoldCancelResult.NotWaiting,
            status == BookHoldStatus.ConvertedToLoan
                ? $"Không thể hủy vì đơn đặt giữ đã ở trạng thái \"{status}\"."
                : $"Chỉ có thể hủy đơn đặt giữ đang ở trạng thái \"{BookHoldStatus.Waiting}\". Đơn này đang ở trạng thái \"{status}\".",
            status);
}

public sealed class ReaderApprovalOutcome
{
    public bool IsSuccess { get; }
    public string? ErrorMessage { get; }
    public LibraryCard? LibraryCard { get; }

    private ReaderApprovalOutcome(bool isSuccess, string? errorMessage, LibraryCard? libraryCard)
        => (IsSuccess, ErrorMessage, LibraryCard) = (isSuccess, errorMessage, libraryCard);

    public static ReaderApprovalOutcome Success(LibraryCard card) => new(true, null, card);
    public static ReaderApprovalOutcome Failed(string message) => new(false, message, null);
}

public sealed class ReaderRejectionOutcome
{
    public bool IsSuccess { get; }
    public string? ErrorMessage { get; }

    private ReaderRejectionOutcome(bool isSuccess, string? errorMessage) => (IsSuccess, ErrorMessage) = (isSuccess, errorMessage);

    public static ReaderRejectionOutcome Success() => new(true, null);
    public static ReaderRejectionOutcome Failed(string message) => new(false, message);
}

public interface IReaderRegistrationService
{
    Task<ReaderRegistrationOutcome> RegisterAsync(ReaderRegistrationViewModel model, CancellationToken cancellationToken = default);
    Task<ReaderAccount?> GetReaderByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<ReaderContactUpdateResult> UpdateReaderContactAsync(
        int id, string phoneNumber, string address, string email, string currentPassword,
        CancellationToken cancellationToken = default);
    Task<ReaderPasswordChangeResult> ChangeReaderPasswordAsync(
        int id, string currentPassword, string newPassword, CancellationToken cancellationToken = default);
    Task<ReaderAccount?> AuthenticateReaderAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<DocumentHoldOutcome> HoldDocumentAsync(int readerAccountId, int documentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReaderBookHoldItem>> GetReaderHoldsAsync(int readerAccountId, CancellationToken cancellationToken = default);
    Task<BookHoldCancelOutcome> CancelReaderHoldAsync(int readerAccountId, long holdId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReaderAccount>> GetPendingReadersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReaderAccount>> GetPendingReadersAsync(string? search, DateOnly? fromDate, DateOnly? toDate, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LibraryCardType>> GetActiveCardTypesAsync(CancellationToken cancellationToken = default);
    Task<ReaderApprovalOutcome> ApproveReaderAsync(ApproveReaderViewModel model, CancellationToken cancellationToken = default);
    Task<ReaderRejectionOutcome> RejectReaderAsync(int readerAccountId, string? rejectionReason, CancellationToken cancellationToken = default);
}
