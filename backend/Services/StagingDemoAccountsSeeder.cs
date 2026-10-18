using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed record StagingDemoAccountsSeedResult(IReadOnlyDictionary<string, bool> Created);

/// <summary>Creates the four documented staging demo identities without changing existing accounts.</summary>
public sealed class StagingDemoAccountsSeeder(
    ApplicationDbContext db,
    IPasswordHasher<AdminAccount> staffPasswordHasher,
    IPasswordHasher<ReaderAccount> readerPasswordHasher)
{
    public const string SystemAdminEmail = "stg-demo-admin@example.test";
    public const string LibraryManagerEmail = "stg-demo-manager@example.test";
    public const string LibrarianEmail = "stg-demo-librarian@example.test";
    public const string ReaderEmail = "stg-demo-reader@example.test";

    public async Task<StagingDemoAccountsSeedResult> SeedAsync(
        IReadOnlyDictionary<string, string?> passwords,
        CancellationToken cancellationToken = default)
    {
        var required = new[] { "SYSTEM_ADMIN", "LIBRARY_MANAGER", "LIBRARIAN", "READER" };
        foreach (var key in required)
        {
            if (!passwords.TryGetValue(key, out var password) || string.IsNullOrWhiteSpace(password) || password.Length < 12)
                throw new InvalidOperationException($"Thiếu STAGING_DEMO_{key}_PASSWORD hoặc mật khẩu ngắn hơn 12 ký tự.");
        }
        if (required.Select(key => passwords[key]).Distinct(StringComparer.Ordinal).Count() != required.Length)
            throw new InvalidOperationException("Bốn tài khoản staging phải dùng bốn mật khẩu khác nhau.");

        var results = new Dictionary<string, bool>(StringComparer.Ordinal);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        results[AccountRoles.SystemAdmin] = await CreateStaffAsync(
            SystemAdminEmail, "Quản trị demo staging", AccountRoles.SystemAdmin, passwords["SYSTEM_ADMIN"]!, cancellationToken);
        results[AccountRoles.LibraryManager] = await CreateStaffAsync(
            LibraryManagerEmail, "Quản lý demo staging", AccountRoles.LibraryManager, passwords["LIBRARY_MANAGER"]!, cancellationToken);
        results[AccountRoles.Librarian] = await CreateStaffAsync(
            LibrarianEmail, "Thủ thư demo staging", AccountRoles.Librarian, passwords["LIBRARIAN"]!, cancellationToken);
        results["Reader"] = await CreateReaderAsync(passwords["READER"]!, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new StagingDemoAccountsSeedResult(results);
    }

    private async Task<bool> CreateStaffAsync(
        string email, string fullName, string role, string password, CancellationToken cancellationToken)
    {
        var existing = await db.AdminAccounts.SingleOrDefaultAsync(account => account.Email == email, cancellationToken);
        if (existing is not null)
        {
            if (existing.Role != role || !existing.IsActive)
                throw new InvalidOperationException($"Email mẫu {email} đã tồn tại nhưng role/trạng thái không phù hợp; tài khoản được giữ nguyên.");
            return false;
        }

        var account = new AdminAccount { Email = email, FullName = fullName, Role = role, IsActive = true };
        account.PasswordHash = staffPasswordHasher.HashPassword(account, password);
        db.AdminAccounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> CreateReaderAsync(string password, CancellationToken cancellationToken)
    {
        var existing = await db.ReaderAccounts.SingleOrDefaultAsync(account => account.Email == ReaderEmail, cancellationToken);
        if (existing is not null)
        {
            if (existing.Status != "Đang hoạt động" || !existing.EmailConfirmed || existing.IsLocked)
                throw new InvalidOperationException($"Email mẫu {ReaderEmail} đã tồn tại nhưng trạng thái không phù hợp; tài khoản được giữ nguyên.");
            return false;
        }

        var now = DateTime.UtcNow;
        var reader = new ReaderAccount
        {
            FullName = "Bạn đọc demo staging",
            DateOfBirth = new DateOnly(2000, 1, 1),
            Email = ReaderEmail,
            PhoneNumber = "0900000000",
            Address = "Dữ liệu giả lập chỉ dùng trong staging.",
            StudentOrStaffCode = "STG-DEMO-LOGIN-RD",
            Status = "Đang hoạt động",
            EmailConfirmed = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        reader.PasswordHash = readerPasswordHasher.HashPassword(reader, password);
        db.ReaderAccounts.Add(reader);
        await db.SaveChangesAsync(cancellationToken);

        var cardTypeId = await db.LibraryCardTypes.Where(type => type.IsActive)
            .OrderBy(type => type.Id).Select(type => (int?)type.Id).FirstOrDefaultAsync(cancellationToken);
        if (cardTypeId.HasValue)
        {
            db.LibraryCards.Add(new LibraryCard
            {
                ReaderAccountId = reader.Id,
                LibraryCardTypeId = cardTypeId.Value,
                CardCode = "STG-DEMO-CARD-LOGIN",
                IssuedOn = DateOnly.FromDateTime(now),
                ExpiresOn = DateOnly.FromDateTime(now.AddYears(1)),
                Status = "Đang hoạt động"
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }
}
