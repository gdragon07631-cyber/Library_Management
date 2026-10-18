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
        var results = new Dictionary<string, bool>(StringComparer.Ordinal);
        var ownsTransaction = db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var needed = new List<string>();
            if (!await db.AdminAccounts.AnyAsync(account => account.Email == SystemAdminEmail, cancellationToken)) needed.Add("SYSTEM_ADMIN");
            if (!await db.AdminAccounts.AnyAsync(account => account.Email == LibraryManagerEmail, cancellationToken)) needed.Add("LIBRARY_MANAGER");
            if (!await db.AdminAccounts.AnyAsync(account => account.Email == LibrarianEmail, cancellationToken)) needed.Add("LIBRARIAN");
            if (!await db.ReaderAccounts.AnyAsync(account => account.Email == ReaderEmail, cancellationToken)) needed.Add("READER");

            foreach (var key in needed)
            {
                if (!passwords.TryGetValue(key, out var password) || string.IsNullOrWhiteSpace(password) || password.Length < 12)
                    throw new InvalidOperationException($"Thiếu STAGING_DEMO_{key}_PASSWORD hoặc mật khẩu ngắn hơn 12 ký tự.");
            }
            var newPasswords = needed.Select(key => passwords[key]).ToArray();
            if (newPasswords.Distinct(StringComparer.Ordinal).Count() != newPasswords.Length)
                throw new InvalidOperationException("Các tài khoản staging mới phải dùng mật khẩu khác nhau.");

            results[AccountRoles.SystemAdmin] = await CreateStaffAsync(
                SystemAdminEmail, "Quản trị demo staging", AccountRoles.SystemAdmin, passwords.GetValueOrDefault("SYSTEM_ADMIN"), cancellationToken);
            results[AccountRoles.LibraryManager] = await CreateStaffAsync(
                LibraryManagerEmail, "Quản lý demo staging", AccountRoles.LibraryManager, passwords.GetValueOrDefault("LIBRARY_MANAGER"), cancellationToken);
            results[AccountRoles.Librarian] = await CreateStaffAsync(
                LibrarianEmail, "Thủ thư demo staging", AccountRoles.Librarian, passwords.GetValueOrDefault("LIBRARIAN"), cancellationToken);
            results["Reader"] = await CreateReaderAsync(passwords.GetValueOrDefault("READER"), cancellationToken);

            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return new StagingDemoAccountsSeedResult(results);
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    private async Task<bool> CreateStaffAsync(
        string email, string fullName, string role, string? password, CancellationToken cancellationToken)
    {
        var existing = await db.AdminAccounts.SingleOrDefaultAsync(account => account.Email == email, cancellationToken);
        if (existing is not null)
        {
            if (existing.Role != role || !existing.IsActive)
                throw new InvalidOperationException($"Email mẫu {email} đã tồn tại nhưng role/trạng thái không phù hợp; tài khoản được giữ nguyên.");
            return false;
        }

        var account = new AdminAccount { Email = email, FullName = fullName, Role = role, IsActive = true };
        account.PasswordHash = staffPasswordHasher.HashPassword(account, password
            ?? throw new InvalidOperationException($"Thiếu mật khẩu để tạo tài khoản mẫu {email}."));
        db.AdminAccounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> CreateReaderAsync(string? password, CancellationToken cancellationToken)
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
        reader.PasswordHash = readerPasswordHasher.HashPassword(reader, password
            ?? throw new InvalidOperationException($"Thiếu mật khẩu để tạo tài khoản mẫu {ReaderEmail}."));
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
