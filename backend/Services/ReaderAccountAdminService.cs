using Microsoft.EntityFrameworkCore;
using System.Data;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed class ReaderAccountAdminService(ApplicationDbContext dbContext) : IReaderAccountAdminService
{
    private const int HistoryLimit = 50;
    private const int CardLockHistoryLimit = 200;

    public async Task<IReadOnlyList<ReaderAccount>> SearchAsync(
        string? keyword, string? status, int limit, CancellationToken cancellationToken = default)
    {
        var query = dbContext.ReaderAccounts.AsNoTracking()
            .Include(reader => reader.LibraryCard).ThenInclude(card => card!.LibraryCardType)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var term = keyword.Trim();
            query = query.Where(reader =>
                reader.FullName.Contains(term) ||
                reader.Email.Contains(term) ||
                reader.PhoneNumber.Contains(term) ||
                reader.StudentOrStaffCode.Contains(term) ||
                (reader.PendingEmail != null && reader.PendingEmail.Contains(term)) ||
                (reader.LibraryCard != null && reader.LibraryCard.CardCode.Contains(term)));
        }

        query = status switch
        {
            ReaderAccountFilters.Locked => query.Where(reader => reader.IsLocked),
            ReaderAccountFilters.UnconfirmedEmail => query.Where(reader => !reader.EmailConfirmed),
            ReaderAccountFilters.PendingEmailChange => query.Where(reader => reader.PendingEmail != null),
            null or "" => query,
            _ => query.Where(reader => reader.Status == status)
        };

        return await query.OrderByDescending(reader => reader.CreatedAtUtc).ThenByDescending(reader => reader.Id)
            .Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<ReaderAccountAdminDetailsViewModel?> GetDetailsAsync(int id, CancellationToken cancellationToken = default)
    {
        var reader = await dbContext.ReaderAccounts.AsNoTracking()
            .Include(item => item.LibraryCard).ThenInclude(card => card!.LibraryCardType)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (reader is null) return null;

        var holds = await dbContext.BookHolds.AsNoTracking()
            .Include(hold => hold.Book)
            .Include(hold => hold.BookCopy)
            .Where(hold => hold.ReaderAccountId == id && BookHoldStatus.ActiveStatuses.Contains(hold.Status))
            .OrderBy(hold => hold.HeldAtUtc)
            .ToListAsync(cancellationToken);

        // Nhật ký ghi bạn đọc dạng "... bạn đọc #<id> (...)", "... bạn đọc #<id>)" hoặc do chính bạn đọc thực hiện.
        var withSpace = $"bạn đọc #{id} ";
        var withParen = $"bạn đọc #{id})";
        var atEnd = $"bạn đọc #{id}";
        var history = await dbContext.AuditLogs.AsNoTracking()
            .Where(log => log.Target.Contains(withSpace) || log.Target.Contains(withParen) ||
                log.Target.EndsWith(atEnd) || log.Actor == reader.Email)
            .OrderByDescending(log => log.OccurredAtUtc).ThenByDescending(log => log.Id)
            .Take(HistoryLimit)
            .ToListAsync(cancellationToken);

        var cardLockHistory = await dbContext.LibraryCardLockHistories.AsNoTracking()
            .Where(entry => entry.ReaderAccountId == id)
            .OrderByDescending(entry => entry.OccurredAtUtc).ThenByDescending(entry => entry.Id)
            .Take(CardLockHistoryLimit)
            .ToListAsync(cancellationToken);

        return new ReaderAccountAdminDetailsViewModel
        {
            Reader = reader,
            Form = ToForm(reader),
            ActiveHolds = holds,
            History = history,
            CardLockHistory = cardLockHistory
        };
    }

    public async Task<ReaderAccountAdminResult> UpdateAsync(
        int id, ReaderAccountAdminFormViewModel model, CancellationToken cancellationToken = default)
    {
        var reader = await dbContext.ReaderAccounts.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (reader is null) return new(ReaderAccountAdminStatus.NotFound, "Không tìm thấy tài khoản bạn đọc.");

        var email = model.Email.Trim();
        var code = model.StudentOrStaffCode.Trim();
        var normalizedEmail = email.ToUpperInvariant();
        var normalizedCode = code.ToUpperInvariant();
        if (await dbContext.ReaderAccounts.AnyAsync(item => item.Id != id && item.Email.Trim().ToUpper() == normalizedEmail, cancellationToken))
            return new(ReaderAccountAdminStatus.DuplicateEmail, "Email này đã được tài khoản bạn đọc khác sử dụng.");
        if (await dbContext.ReaderAccounts.AnyAsync(item => item.Id != id && item.StudentOrStaffCode.Trim().ToUpper() == normalizedCode, cancellationToken))
            return new(ReaderAccountAdminStatus.DuplicateCode, "Mã sinh viên / mã cán bộ này đã được tài khoản bạn đọc khác sử dụng.");

        var changes = new List<string>();
        void Track(string label, string? before, string? after, Action apply)
        {
            if (string.Equals(before?.Trim() ?? string.Empty, after?.Trim() ?? string.Empty, StringComparison.Ordinal)) return;
            changes.Add($"{label}: {Display(before)} → {Display(after)}");
            apply();
        }

        Track("họ tên", reader.FullName, model.FullName, () => reader.FullName = model.FullName.Trim());
        Track("ngày sinh", reader.DateOfBirth.ToString("dd/MM/yyyy"), model.DateOfBirth!.Value.ToString("dd/MM/yyyy"),
            () => reader.DateOfBirth = model.DateOfBirth.Value);
        Track("số điện thoại", reader.PhoneNumber, model.PhoneNumber, () => reader.PhoneNumber = model.PhoneNumber.Trim());
        Track("địa chỉ", reader.Address, model.Address,
            () => reader.Address = string.IsNullOrWhiteSpace(model.Address) ? null : model.Address.Trim());
        Track("mã SV/CB", reader.StudentOrStaffCode, code, () => reader.StudentOrStaffCode = code);
        if (!string.Equals(reader.Email.Trim(), email, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add($"email: {reader.Email} → {email}");
            reader.Email = email;
            // Quản trị đã kiểm tra trực tiếp với bạn đọc: email mới có hiệu lực ngay, huỷ yêu cầu đổi email đang chờ.
            reader.EmailConfirmed = true;
            reader.PendingEmail = null;
        }

        if (changes.Count == 0) return new(ReaderAccountAdminStatus.NoChange, "Không có thông tin nào thay đổi.", reader);

        reader.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(ReaderAccountAdminStatus.Success, Reader: reader, Changes: changes);
    }

    public async Task<ReaderAccountAdminResult> SetLockedAsync(
        int id, bool locked, string? reason, CancellationToken cancellationToken = default)
    {
        var trimmedReason = reason?.Trim();
        if (locked && string.IsNullOrWhiteSpace(trimmedReason))
            return new(ReaderAccountAdminStatus.MissingReason, "Vui lòng nhập lý do khoá tài khoản.");
        if (trimmedReason is { Length: > 500 })
            return new(ReaderAccountAdminStatus.MissingReason, "Lý do không được vượt quá 500 ký tự.");

        var reader = await dbContext.ReaderAccounts.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (reader is null) return new(ReaderAccountAdminStatus.NotFound, "Không tìm thấy tài khoản bạn đọc.");
        if (reader.IsLocked == locked)
            return new(ReaderAccountAdminStatus.NoChange,
                locked ? "Tài khoản đã bị khoá từ trước." : "Tài khoản đang không bị khoá.", reader);

        reader.IsLocked = locked;
        reader.LockReason = locked ? trimmedReason : null;
        // Khoá thì thu hồi ngay mọi phiên đăng nhập đang mở của bạn đọc.
        if (locked) reader.SessionVersion++;
        reader.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(ReaderAccountAdminStatus.Success, Reader: reader);
    }

    public async Task<ReaderAccountAdminResult> SetCardLockedAsync(
        int readerId,
        string? reason,
        DateTime? lockExpiresAtUtc,
        bool manualUnlock,
        int lockedByAdminAccountId,
        CancellationToken cancellationToken = default)
    {
        var trimmedReason = reason?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedReason))
            return new(ReaderAccountAdminStatus.MissingReason, "Vui lòng nhập lý do khoá thẻ.");
        if (trimmedReason.Length > 1000)
            return new(ReaderAccountAdminStatus.MissingReason, "Lý do khoá thẻ không được vượt quá 1000 ký tự.");

        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var reader = await dbContext.ReaderAccounts
            .Include(item => item.LibraryCard)
            .SingleOrDefaultAsync(item => item.Id == readerId, cancellationToken);
        if (reader is null) return new(ReaderAccountAdminStatus.NotFound, "Không tìm thấy bạn đọc.");
        if (reader.LibraryCard is null)
            return new(ReaderAccountAdminStatus.NotFound, "Bạn đọc chưa được cấp thẻ. Không thể khoá thẻ.");

        var card = reader.LibraryCard;
        var lockStartedAtUtc = DateTime.UtcNow;
        if (manualUnlock)
        {
            if (LibraryCardStatusHelper.IsCardLocked(card, lockStartedAtUtc))
                return new(ReaderAccountAdminStatus.NoChange, "Thẻ đang bị khoá từ trước.", reader);
        }
        else if (lockExpiresAtUtc is null)
        {
            return new(ReaderAccountAdminStatus.MissingReason, "Vui lòng chọn thời hạn khoá hoặc chọn hình thức khoá đến khi mở thủ công.");
        }
        else if (lockExpiresAtUtc.Value <= lockStartedAtUtc)
        {
            return new(ReaderAccountAdminStatus.MissingReason, "Thời hạn khoá phải sau thời điểm bắt đầu khoá.");
        }

        if (LibraryCardStatusHelper.IsCardLocked(card, lockStartedAtUtc))
            return new(ReaderAccountAdminStatus.NoChange, "Thẻ đang bị khoá từ trước.", reader);

        card.Status = "Đang bị khoá";
        card.LockReason = trimmedReason;
        card.LockedAtUtc = lockStartedAtUtc;
        card.LockExpiresAtUtc = manualUnlock ? null : lockExpiresAtUtc;
        card.IsLockUntilManualUnlock = manualUnlock;
        card.LockedByAdminAccountId = lockedByAdminAccountId;
        reader.UpdatedAtUtc = DateTime.UtcNow;

        // Nhật ký khoá được lưu cùng giao dịch với trạng thái thẻ: không có thẻ bị khoá mà thiếu nhật ký.
        var performedBy = await dbContext.AdminAccounts.AsNoTracking()
            .Where(admin => admin.Id == lockedByAdminAccountId)
            .Select(admin => admin.Email)
            .SingleOrDefaultAsync(cancellationToken);
        dbContext.LibraryCardLockHistories.Add(new LibraryCardLockHistory
        {
            LibraryCardId = card.Id,
            CardCode = card.CardCode,
            ReaderAccountId = reader.Id,
            ReaderName = reader.FullName,
            EventType = LibraryCardLockEvents.Lock,
            OccurredAtUtc = lockStartedAtUtc,
            PerformedByAdminAccountId = lockedByAdminAccountId,
            PerformedBy = performedBy ?? $"Nhân sự #{lockedByAdminAccountId}",
            LockReason = trimmedReason,
            LockedAtUtc = lockStartedAtUtc,
            LockExpiresAtUtc = card.LockExpiresAtUtc,
            IsLockUntilManualUnlock = manualUnlock
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(ReaderAccountAdminStatus.Success, Reader: reader);
    }

    public async Task<ReaderAccountAdminResult> SetCardUnlockedAsync(
        int readerId,
        string? reason,
        int unlockedByAdminAccountId,
        string actorEmail,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var trimmedReason = reason?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedReason))
            return new(ReaderAccountAdminStatus.MissingReason, "Vui lòng nhập lý do mở khoá thẻ.");
        if (trimmedReason.Length > 1000)
            return new(ReaderAccountAdminStatus.MissingReason, "Lý do mở khoá thẻ không được vượt quá 1000 ký tự.");

        var reader = await dbContext.ReaderAccounts.AsNoTracking()
            .Include(item => item.LibraryCard)
            .SingleOrDefaultAsync(item => item.Id == readerId, cancellationToken);
        if (reader is null)
            return new(ReaderAccountAdminStatus.NotFound, "Không tìm thấy bạn đọc.");

        var card = reader.LibraryCard;
        if (card is null)
            return new(ReaderAccountAdminStatus.NotFound, "Bạn đọc chưa được cấp thẻ. Không thể mở khoá thẻ.");

        var now = DateTime.UtcNow;
        if (!LibraryCardStatusHelper.IsCardLocked(card, now))
        {
            // Khoá có thời hạn đã tự hết hạn: không ghi thêm một lần mở khoá cho trạng thái đã hoạt động.
            var message = card.LockExpiresAtUtc is { } expired && card.LockedAtUtc.HasValue
                ? $"Thẻ đã hết hạn khoá từ {DateTime.SpecifyKind(expired, DateTimeKind.Utc).ToLocalTime():dd/MM/yyyy HH:mm} và đang hoạt động, không cần mở khoá."
                : "Thẻ đang hoạt động, không cần mở khoá thêm.";
            return new(ReaderAccountAdminStatus.NoChange, message, reader);
        }

        var previous = (card.Status, card.LockReason, card.LockedAtUtc, card.LockExpiresAtUtc, card.IsLockUntilManualUnlock);
        var previousReason = string.IsNullOrWhiteSpace(previous.LockReason) ? "Không có lý do cụ thể." : previous.LockReason.Trim();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        // Chỉ cập nhật khi thẻ vẫn đúng trạng thái khoá vừa đọc: hai yêu cầu mở khoá đồng thời chỉ một yêu cầu thành công,
        // tránh ghi hai lần mở khoá cho cùng một lần khoá.
        var affected = await dbContext.LibraryCards
            .Where(item => item.Id == card.Id && item.Status == previous.Status && item.LockedAtUtc == previous.LockedAtUtc)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, "Đang hoạt động")
                .SetProperty(item => item.LockReason, (string?)null)
                .SetProperty(item => item.LockedAtUtc, (DateTime?)null)
                .SetProperty(item => item.LockExpiresAtUtc, (DateTime?)null)
                .SetProperty(item => item.IsLockUntilManualUnlock, false)
                .SetProperty(item => item.LockedByAdminAccountId, (int?)null), cancellationToken);
        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(ReaderAccountAdminStatus.NoChange, "Thẻ vừa được cập nhật bởi thao tác khác. Vui lòng tải lại trang để xem trạng thái mới nhất.", reader);
        }

        await dbContext.ReaderAccounts.Where(item => item.Id == reader.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.UpdatedAtUtc, now), cancellationToken);
        dbContext.LibraryCardLockHistories.Add(new LibraryCardLockHistory
        {
            LibraryCardId = card.Id,
            CardCode = card.CardCode,
            ReaderAccountId = reader.Id,
            ReaderName = reader.FullName,
            EventType = LibraryCardLockEvents.Unlock,
            OccurredAtUtc = now,
            PerformedByAdminAccountId = unlockedByAdminAccountId,
            PerformedBy = actorEmail,
            LockReason = previous.LockReason?.Trim(),
            UnlockReason = trimmedReason,
            LockedAtUtc = previous.LockedAtUtc,
            LockExpiresAtUtc = previous.LockExpiresAtUtc,
            IsLockUntilManualUnlock = previous.IsLockUntilManualUnlock
        });
        dbContext.AuditLogs.Add(AuditLogService.Create(
            actorEmail,
            AuditActions.UnlockCard,
            $"Thẻ bạn đọc #{reader.Id} ({reader.Email}) – mã thẻ {card.CardCode} – lý do khóa trước đó: {previousReason} – lý do mở khóa: {trimmedReason}",
            ipAddress));

        // Lỗi ở bất kỳ bước nào sẽ huỷ giao dịch: thẻ vẫn khoá và không có nhật ký mở khoá dở dang.
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        card.Status = "Đang hoạt động";
        card.LockReason = null;
        card.LockedAtUtc = null;
        card.LockExpiresAtUtc = null;
        card.IsLockUntilManualUnlock = false;
        card.LockedByAdminAccountId = null;
        return new(ReaderAccountAdminStatus.Success, Reader: reader);
    }

    public async Task<IReadOnlyList<LibraryCardLockHistory>> SearchCardLockHistoryAsync(
        LibraryCardLockHistoryFilter filter, int limit, CancellationToken cancellationToken = default)
    {
        var query = dbContext.LibraryCardLockHistories.AsNoTracking();
        if (filter.ReaderAccountId is int readerId)
            query = query.Where(entry => entry.ReaderAccountId == readerId);
        if (!string.IsNullOrWhiteSpace(filter.Q))
        {
            var term = filter.Q.Trim();
            query = query.Where(entry => entry.CardCode.Contains(term) || entry.ReaderName.Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(filter.EventType))
            query = query.Where(entry => entry.EventType == filter.EventType);
        // Ngày lọc theo giờ thư viện; dữ liệu lưu UTC.
        if (filter.FromDate is DateOnly from)
        {
            var fromUtc = TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local);
            query = query.Where(entry => entry.OccurredAtUtc >= fromUtc);
        }
        if (filter.ToDate is DateOnly to)
        {
            var toExclusiveUtc = TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local);
            query = query.Where(entry => entry.OccurredAtUtc < toExclusiveUtc);
        }

        return await query.OrderByDescending(entry => entry.OccurredAtUtc).ThenByDescending(entry => entry.Id)
            .Take(limit).ToListAsync(cancellationToken);
    }

    public Task<LibraryCardLockHistory?> GetCardLockHistoryAsync(long id, CancellationToken cancellationToken = default) =>
        dbContext.LibraryCardLockHistories.AsNoTracking().SingleOrDefaultAsync(entry => entry.Id == id, cancellationToken);

    public async Task<ReaderAccountAdminResult> RevokeSessionsAsync(int id, CancellationToken cancellationToken = default)
    {
        var reader = await dbContext.ReaderAccounts.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (reader is null) return new(ReaderAccountAdminStatus.NotFound, "Không tìm thấy tài khoản bạn đọc.");
        reader.SessionVersion++;
        reader.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(ReaderAccountAdminStatus.Success, Reader: reader);
    }

    public static ReaderAccountAdminFormViewModel ToForm(ReaderAccount reader) => new()
    {
        Id = reader.Id,
        FullName = reader.FullName,
        DateOfBirth = reader.DateOfBirth,
        Email = reader.Email,
        PhoneNumber = reader.PhoneNumber,
        Address = reader.Address,
        StudentOrStaffCode = reader.StudentOrStaffCode
    };

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "(trống)" : value.Trim();
}

/// <summary>Bộ lọc đặc biệt trên trang Tài khoản bạn đọc (ngoài các trạng thái tài khoản).</summary>
public static class ReaderAccountFilters
{
    public const string Locked = "locked";
    public const string UnconfirmedEmail = "unconfirmed";
    public const string PendingEmailChange = "pending-email";
}
