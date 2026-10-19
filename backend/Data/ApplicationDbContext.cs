using Microsoft.EntityFrameworkCore;
using Project.Models;

namespace Project.Data;

/// <summary>
/// EF Core context for the application. Business entities can be added here as stories are implemented.
/// </summary>
public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<AdminAccount> AdminAccounts => Set<AdminAccount>();
    public DbSet<LoginLog> LoginLogs => Set<LoginLog>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ReaderAccount> ReaderAccounts => Set<ReaderAccount>();
    public DbSet<ReaderPasswordHistory> ReaderPasswordHistories => Set<ReaderPasswordHistory>();
    public DbSet<ReaderPasswordResetToken> ReaderPasswordResetTokens => Set<ReaderPasswordResetToken>();
    public DbSet<ReaderEmailVerificationToken> ReaderEmailVerificationTokens => Set<ReaderEmailVerificationToken>();
    public DbSet<ReaderPasswordResetRequest> ReaderPasswordResetRequests => Set<ReaderPasswordResetRequest>();
    public DbSet<Author> Authors => Set<Author>();
    public DbSet<Book> Books => Set<Book>();
    public DbSet<BookAuthor> BookAuthors => Set<BookAuthor>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<LibraryCardType> LibraryCardTypes => Set<LibraryCardType>();
    public DbSet<LibraryCard> LibraryCards => Set<LibraryCard>();
    public DbSet<BookHold> BookHolds => Set<BookHold>();
    public DbSet<HoldPickupExpiryRun> HoldPickupExpiryRuns => Set<HoldPickupExpiryRun>();
    public DbSet<BookLoan> BookLoans => Set<BookLoan>();
    public DbSet<DirectLoanOperation> DirectLoanOperations => Set<DirectLoanOperation>();
    public DbSet<DirectLoanOperationItem> DirectLoanOperationItems => Set<DirectLoanOperationItem>();
    public DbSet<LoanContactHistory> LoanContactHistories => Set<LoanContactHistory>();
    public DbSet<BookCopy> BookCopies => Set<BookCopy>();
    public DbSet<BookCopyStatusHistory> BookCopyStatusHistories => Set<BookCopyStatusHistory>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Shelf> Shelves => Set<Shelf>();
    public DbSet<WeeklyWorkingSchedule> WeeklyWorkingSchedules => Set<WeeklyWorkingSchedule>();
    public DbSet<HolidayClosure> HolidayClosures => Set<HolidayClosure>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<LoanPolicy> LoanPolicies => Set<LoanPolicy>();
    public DbSet<StaffPasswordSetupToken> StaffPasswordSetupTokens => Set<StaffPasswordSetupToken>();
    public DbSet<LibraryCardLockHistory> LibraryCardLockHistories => Set<LibraryCardLockHistory>();

    public const string AuditLogReadOnlyTrigger = "TR_AuditLogs_ReadOnly";
    public const string CardLockHistoryReadOnlyTrigger = "TR_LibraryCardLockHistories_ReadOnly";

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureAuditLogsAreAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsureAuditLogsAreAppendOnly();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Nhật ký hoạt động chỉ được thêm mới; mọi thao tác sửa/xóa qua hệ thống đều bị từ chối.</summary>
    private void EnsureAuditLogsAreAppendOnly()
    {
        if (ChangeTracker.Entries<AuditLog>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Nhật ký hoạt động là dữ liệu chỉ đọc, không được sửa hoặc xóa.");
        if (ChangeTracker.Entries<LibraryCardLockHistory>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Nhật ký khoá/mở khoá thẻ là dữ liệu chỉ đọc, không được sửa hoặc xóa.");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<DirectLoanOperation>(entity =>
        {
            entity.HasKey(x => x.RequestId);
            entity.Property(x => x.RequestId).ValueGeneratedNever();
            entity.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CardCode).HasMaxLength(32).IsRequired();
            entity.Property(x => x.CopyCodesJson).IsRequired();
            entity.Property(x => x.RowIdsJson).IsRequired();
            entity.HasOne<AdminAccount>().WithMany().HasForeignKey(x => x.StaffId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<DirectLoanOperationItem>(entity =>
        {
            entity.HasKey(x => new { x.RequestId, x.Position });
            entity.HasIndex(x => x.BookLoanId).IsUnique();
            entity.HasOne<DirectLoanOperation>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.BookLoan).WithMany().HasForeignKey(x => x.BookLoanId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Author>(entity =>
        {
            entity.HasIndex(author => author.Name).IsUnique();
            entity.Property(author => author.Name).HasMaxLength(150).IsRequired();
            entity.Property(author => author.Note).HasMaxLength(500);
            entity.Property(author => author.Status).HasMaxLength(50).HasDefaultValue("Hoạt động").IsRequired();
            entity.Property(author => author.CreatedAtUtc).HasColumnType("datetime2");
        });

        modelBuilder.Entity<Book>(entity =>
        {
            entity.Property(b => b.Title).HasMaxLength(250).IsRequired();
            entity.Property(b => b.Subtitle).HasMaxLength(250);
            entity.Property(b => b.Isbn).HasMaxLength(50);
            entity.Property(b => b.Publisher).HasMaxLength(200);
            entity.Property(b => b.Description).HasMaxLength(500);
            entity.Property(b => b.CoverImagePath).HasMaxLength(500);
            entity.Property(b => b.ThumbnailImagePath).HasMaxLength(500);
            entity.Property(b => b.CreatedAtUtc).HasColumnType("datetime2");
            entity.HasOne(b => b.Author)
                .WithMany(a => a.Books)
                .HasForeignKey(b => b.AuthorId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(b => b.Category)
                .WithMany(c => c.Books)
                .HasForeignKey(b => b.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BookAuthor>(entity =>
        {
            entity.HasKey(link => new { link.BookId, link.AuthorId });
            entity.HasIndex(link => link.AuthorId);
            entity.HasOne(link => link.Book)
                .WithMany(book => book.BookAuthors)
                .HasForeignKey(link => link.BookId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(link => link.Author)
                .WithMany(author => author.BookAuthors)
                .HasForeignKey(link => link.AuthorId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Category>(entity =>
        {
            entity.HasIndex(category => category.Name).IsUnique();
            entity.Property(category => category.Name).HasMaxLength(150).IsRequired();
            entity.Property(category => category.Status).HasMaxLength(50).HasDefaultValue("Hoạt động").IsRequired();
            entity.Property(category => category.CreatedAtUtc).HasColumnType("datetime2");
            entity.HasOne(category => category.Parent)
                .WithMany(category => category.Children)
                .HasForeignKey(category => category.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AdminAccount>(entity =>
        {
            entity.HasIndex(account => account.Email).IsUnique();
            entity.Property(account => account.Email).HasMaxLength(256).IsRequired();
            entity.Property(account => account.PasswordHash).HasMaxLength(512).IsRequired();
            entity.Property(account => account.Role).HasMaxLength(30).HasDefaultValue(AccountRoles.SystemAdmin).IsRequired();
            entity.Property(account => account.IsActive).HasDefaultValue(true);
            entity.Property(account => account.FailedLoginAttempts).HasDefaultValue(0);
            entity.Property(account => account.FullName).HasMaxLength(100).HasDefaultValue(string.Empty).IsRequired();
            entity.Property(account => account.PhoneNumber).HasMaxLength(20);
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_AdminAccounts_FailedLoginAttempts_NonNegative", "[FailedLoginAttempts] >= 0");
                table.HasCheckConstraint(
                    "CK_AdminAccounts_Role_Valid",
                    $"[Role] IN ('{AccountRoles.Librarian}', '{AccountRoles.LibraryManager}', '{AccountRoles.SystemAdmin}')");
            });
        });

        modelBuilder.Entity<StaffPasswordSetupToken>(entity =>
        {
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.HasIndex(token => new { token.AdminAccountId, token.ExpiresAtUtc });
            entity.Property(token => token.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(token => token.CreatedAtUtc).HasColumnType("datetime2");
            entity.Property(token => token.ExpiresAtUtc).HasColumnType("datetime2");
            entity.Property(token => token.UsedAtUtc).HasColumnType("datetime2");
            entity.HasOne(token => token.AdminAccount)
                .WithMany()
                .HasForeignKey(token => token.AdminAccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LoginLog>(entity =>
        {
            entity.HasIndex(log => log.AttemptedAtUtc);
            entity.HasIndex(log => log.Email);
            entity.Property(log => log.Email).HasMaxLength(256).IsRequired();
            entity.Property(log => log.IpAddress).HasMaxLength(45);
            entity.Property(log => log.Status).HasMaxLength(32).IsRequired();
            entity.HasOne(log => log.AdminAccount)
                .WithMany()
                .HasForeignKey(log => log.AdminAccountId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.HasIndex(token => new { token.AdminAccountId, token.ExpiresAtUtc });
            entity.Property(token => token.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(token => token.ReplacedByTokenHash).HasMaxLength(64);
            entity.HasOne(token => token.AdminAccount)
                .WithMany()
                .HasForeignKey(token => token.AdminAccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReaderAccount>(entity =>
        {
            entity.HasIndex(reader => reader.Email);
            entity.HasIndex(reader => reader.StudentOrStaffCode);
            entity.Property(reader => reader.FullName).HasMaxLength(100).IsRequired();
            entity.Property(reader => reader.Email).HasMaxLength(256).IsRequired();
            entity.Property(reader => reader.PhoneNumber).HasMaxLength(20).IsRequired();
            entity.Property(reader => reader.Address).HasMaxLength(500);
            entity.Property(reader => reader.StudentOrStaffCode).HasMaxLength(50).IsRequired();
            entity.Property(reader => reader.PasswordHash).HasMaxLength(512).IsRequired();
            entity.Property(reader => reader.SessionVersion).HasDefaultValue(0);
            entity.Property(reader => reader.Status).HasMaxLength(50).HasDefaultValue("Chờ duyệt").IsRequired();
            entity.Property(reader => reader.RejectionReason).HasMaxLength(1000);
            entity.Property(reader => reader.CreatedAtUtc).HasColumnType("datetime2");
            entity.Property(reader => reader.UpdatedAtUtc).HasColumnType("datetime2");
            entity.Property(reader => reader.OutstandingBalance).HasColumnType("decimal(18,2)").HasDefaultValue(0m);
        });

        modelBuilder.Entity<LibraryCardType>(entity =>
        {
            entity.HasIndex(type => type.Name).IsUnique();
            entity.Property(type => type.Name).HasMaxLength(100).IsRequired();
            entity.Property(type => type.IsActive).HasDefaultValue(true);
            entity.Property(type => type.MaxRenewals).HasDefaultValue(LibraryCardType.DefaultMaxRenewals);
        });

        modelBuilder.Entity<ReaderPasswordHistory>(entity =>
        {
            entity.HasIndex(history => new { history.ReaderAccountId, history.CreatedAtUtc });
            entity.Property(history => history.PasswordHash).HasMaxLength(512).IsRequired();
            entity.Property(history => history.CreatedAtUtc).HasColumnType("datetime2");
            entity.HasOne(history => history.ReaderAccount).WithMany(reader => reader.PasswordHistories)
                .HasForeignKey(history => history.ReaderAccountId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LibraryCard>(entity =>
        {
            entity.HasIndex(card => card.CardCode).IsUnique();
            entity.HasIndex(card => card.ReaderAccountId).IsUnique();
            entity.Property(card => card.CardCode).HasMaxLength(32).IsRequired();
            entity.Property(card => card.Status).HasMaxLength(50).HasDefaultValue("Đang hoạt động").IsRequired();
            entity.Property(card => card.LockReason).HasMaxLength(1000);
            entity.Property(card => card.LockedAtUtc).HasColumnType("datetime2");
            entity.Property(card => card.LockExpiresAtUtc).HasColumnType("datetime2");
            entity.HasOne(card => card.ReaderAccount).WithOne(reader => reader.LibraryCard)
                .HasForeignKey<LibraryCard>(card => card.ReaderAccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(card => card.LibraryCardType).WithMany(type => type.LibraryCards)
                .HasForeignKey(card => card.LibraryCardTypeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(card => card.LockedByAdminAccount).WithMany()
                .HasForeignKey(card => card.LockedByAdminAccountId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<BookHold>(entity =>
        {
            entity.HasIndex(hold => hold.ExpiredBookCopyId);
            entity.HasIndex(hold => new { hold.ReaderAccountId, hold.Status, hold.BookId });
            entity.Property(hold => hold.HeldAtUtc).HasColumnType("datetime2");
            entity.Property(hold => hold.Status).HasMaxLength(50).HasDefaultValue(BookHoldStatus.Waiting).IsRequired();
            entity.Property(hold => hold.PickupDeadlineUtc).HasColumnType("datetime2");
            entity.Property(hold => hold.CancellationReason).HasMaxLength(1000);
            entity.Property(hold => hold.CancelledAtUtc).HasColumnType("datetime2");
            entity.Property(hold => hold.ExpiredBookCopyCode).HasMaxLength(50);
            entity.Property(hold => hold.ExpiryCopyOutcome).HasMaxLength(30);
            entity.HasOne(hold => hold.ReaderAccount).WithMany(reader => reader.BookHolds)
                .HasForeignKey(hold => hold.ReaderAccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(hold => hold.Book).WithMany()
                .HasForeignKey(hold => hold.BookId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(hold => hold.BookCopy).WithMany()
                .HasForeignKey(hold => hold.BookCopyId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<HoldPickupExpiryRun>(entity =>
        {
            entity.HasIndex(run => run.BusinessDate).IsUnique();
            entity.HasIndex(run => run.StartedAtUtc);
            entity.Property(run => run.BusinessDate).HasColumnType("date").IsRequired();
            entity.Property(run => run.StartedAtUtc).HasColumnType("datetime2").IsRequired();
            entity.Property(run => run.CompletedAtUtc).HasColumnType("datetime2");
            entity.Property(run => run.Status).HasMaxLength(20).IsRequired();
            entity.Property(run => run.ErrorMessage).HasMaxLength(2000);
            entity.Property(run => run.ResultMessage).HasMaxLength(500);
        });

        modelBuilder.Entity<Warehouse>(entity =>
        {
            entity.HasIndex(w => w.Code).IsUnique();
            entity.Property(w => w.Code).HasMaxLength(50).IsRequired();
            entity.Property(w => w.Name).HasMaxLength(150).IsRequired();
            entity.Property(w => w.Address).HasMaxLength(500);
            entity.Property(w => w.Description).HasMaxLength(500);
            entity.Property(w => w.Status).HasMaxLength(50).HasDefaultValue("Hoạt động").IsRequired();
            entity.Property(w => w.CreatedAtUtc).HasColumnType("datetime2");
        });

        modelBuilder.Entity<Shelf>(entity =>
        {
            entity.HasIndex(s => new { s.WarehouseId, s.Code }).IsUnique();
            entity.Property(s => s.Code).HasMaxLength(50).IsRequired();
            entity.Property(s => s.Name).HasMaxLength(150).IsRequired();
            entity.Property(s => s.Description).HasMaxLength(500);
            entity.Property(s => s.Status).HasMaxLength(50).HasDefaultValue("Hoạt động").IsRequired();
            entity.Property(s => s.CreatedAtUtc).HasColumnType("datetime2");
            entity.HasOne(s => s.Warehouse)
                .WithMany(w => w.Shelves)
                .HasForeignKey(s => s.WarehouseId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BookLoan>(entity =>
        {
            entity.Property(loan => loan.LoanDate).HasColumnType("date");
            entity.Property(loan => loan.OriginalDueDate).HasColumnType("date");
            entity.Property(loan => loan.DueDate).HasColumnType("date");
            entity.Property(loan => loan.RenewalCount).HasDefaultValue(0);
            entity.Property(loan => loan.CreatedAtUtc).HasColumnType("datetime2");
            entity.Property(loan => loan.ReturnedAtUtc).HasColumnType("datetime2");
            entity.HasOne(loan => loan.Book).WithMany()
                .HasForeignKey(loan => loan.BookId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(loan => loan.ReaderAccount).WithMany()
                .HasForeignKey(loan => loan.ReaderAccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(loan => loan.BookCopyId).IsUnique().HasFilter("[BookCopyId] IS NOT NULL");
            entity.HasOne(loan => loan.BookCopy).WithMany()
                .HasForeignKey(loan => loan.BookCopyId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(loan => loan.SourceBookHoldId).IsUnique().HasFilter("[SourceBookHoldId] IS NOT NULL");
            entity.HasOne(loan => loan.SourceBookHold).WithMany()
                .HasForeignKey(loan => loan.SourceBookHoldId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(loan => loan.CreatedByAdminAccount).WithMany()
                .HasForeignKey(loan => loan.CreatedByAdminAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LoanContactHistory>(entity =>
        {
            entity.HasIndex(history => new { history.BookLoanId, history.CreatedAtUtc });
            entity.Property(history => history.CreatedAtUtc).HasColumnType("datetime2");
            entity.Property(history => history.Note).HasMaxLength(1000).IsRequired();
            entity.Property(history => history.ContactedBy).HasMaxLength(256).IsRequired();
            entity.HasOne(history => history.BookLoan).WithMany(loan => loan.ContactHistories)
                .HasForeignKey(history => history.BookLoanId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(history => history.ContactedByAdminAccount).WithMany()
                .HasForeignKey(history => history.ContactedByAdminAccountId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<BookCopy>(entity =>
        {
            entity.HasIndex(copy => copy.CopyCode).IsUnique();
            entity.HasIndex(copy => copy.ShelfId);
            entity.Property(copy => copy.CopyCode).HasMaxLength(50).IsRequired();
            entity.Property(copy => copy.ReceivedDate).HasColumnType("date");
            entity.Property(copy => copy.CoverPrice).HasColumnType("decimal(18,2)");
            entity.Property(copy => copy.Status).HasMaxLength(50).HasDefaultValue("Sẵn sàng").IsRequired();
            entity.HasOne(copy => copy.Book).WithMany()
                .HasForeignKey(copy => copy.BookId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(copy => copy.Shelf).WithMany()
                .HasForeignKey(copy => copy.ShelfId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(copy => copy.PhysicalCondition).HasMaxLength(50).HasDefaultValue(BookCopyCondition.Good).IsRequired();
            entity.Property(copy => copy.Note).HasMaxLength(500);
            entity.Property(copy => copy.StatusReason).HasMaxLength(500);
        });

        modelBuilder.Entity<BookCopyStatusHistory>(entity =>
        {
            entity.HasIndex(history => new { history.BookCopyId, history.ChangedAtUtc });
            entity.Property(history => history.FromStatus).HasMaxLength(50).IsRequired();
            entity.Property(history => history.ToStatus).HasMaxLength(50).IsRequired();
            entity.Property(history => history.Reason).HasMaxLength(500).IsRequired();
            entity.Property(history => history.ChangedBy).HasMaxLength(256).IsRequired();
            entity.Property(history => history.ChangedAtUtc).HasColumnType("datetime2");
            entity.HasOne(history => history.BookCopy).WithMany()
                .HasForeignKey(history => history.BookCopyId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WeeklyWorkingSchedule>(entity =>
        {
            entity.HasIndex(schedule => schedule.DayOfWeek).IsUnique();
            entity.Property(schedule => schedule.DayOfWeek).HasConversion<int>().IsRequired();
            entity.Property(schedule => schedule.IsOpen).HasDefaultValue(true);
            entity.Property(schedule => schedule.Note).HasMaxLength(500);
        });

        modelBuilder.Entity<HolidayClosure>(entity =>
        {
            entity.HasIndex(holiday => holiday.HolidayDate).IsUnique();
            entity.Property(holiday => holiday.HolidayDate).HasColumnType("date").IsRequired();
            entity.Property(holiday => holiday.Reason).HasMaxLength(150).IsRequired();
            entity.Property(holiday => holiday.Note).HasMaxLength(500);
        });

        modelBuilder.Entity<ReaderEmailVerificationToken>(entity =>
        {
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.HasIndex(token => new { token.ReaderAccountId, token.CreatedAtUtc });
            entity.Property(token => token.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(token => token.NewEmail).HasMaxLength(256);
            entity.Property(token => token.CreatedAtUtc).HasColumnType("datetime2");
            entity.Property(token => token.ExpiresAtUtc).HasColumnType("datetime2");
            entity.Property(token => token.UsedAtUtc).HasColumnType("datetime2");
            entity.HasOne(token => token.ReaderAccount)
                .WithMany()
                .HasForeignKey(token => token.ReaderAccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReaderPasswordResetToken>(entity =>
        {
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.HasIndex(token => new { token.ReaderAccountId, token.ExpiresAtUtc });
            entity.Property(token => token.TokenHash).HasMaxLength(64).IsRequired();
            entity.HasOne(token => token.ReaderAccount)
                .WithMany()
                .HasForeignKey(token => token.ReaderAccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReaderPasswordResetRequest>(entity =>
        {
            entity.HasIndex(request => new { request.EmailHash, request.RequestedAtUtc });
            entity.Property(request => request.EmailHash).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            // The database trigger rejects UPDATE/DELETE; declaring it stops EF from using OUTPUT on INSERT.
            entity.ToTable(table => table.HasTrigger(AuditLogReadOnlyTrigger));
            entity.HasIndex(log => log.OccurredAtUtc);
            entity.Property(log => log.OccurredAtUtc).HasColumnType("datetime2");
            entity.Property(log => log.Actor).HasMaxLength(256).IsRequired();
            entity.Property(log => log.Action).HasMaxLength(100).IsRequired();
            entity.Property(log => log.Target).HasMaxLength(500).IsRequired();
            entity.Property(log => log.IpAddress).HasMaxLength(45).IsRequired();
        });

        modelBuilder.Entity<LibraryCardLockHistory>(entity =>
        {
            // Không đặt khoá ngoại: nhật ký phải giữ nguyên kể cả khi thẻ, bạn đọc hoặc nhân sự bị thay đổi/xoá.
            entity.ToTable(table => table.HasTrigger(CardLockHistoryReadOnlyTrigger));
            entity.HasIndex(history => history.OccurredAtUtc);
            entity.HasIndex(history => new { history.ReaderAccountId, history.OccurredAtUtc });
            entity.HasIndex(history => history.CardCode);
            entity.Property(history => history.OccurredAtUtc).HasColumnType("datetime2");
            entity.Property(history => history.LockedAtUtc).HasColumnType("datetime2");
            entity.Property(history => history.LockExpiresAtUtc).HasColumnType("datetime2");
        });

        modelBuilder.Entity<LoanPolicy>(entity =>
        {
            entity.Property(policy => policy.Id).ValueGeneratedNever();
            entity.Property(policy => policy.UpdatedAtUtc).HasColumnType("datetime2");
            entity.Property(policy => policy.DebtLockThreshold).HasColumnType("decimal(18,2)").HasDefaultValue(LoanPolicy.DefaultDebtLockThreshold);
            entity.HasData(new LoanPolicy
            {
                Id = LoanPolicy.SingletonId,
                LoanDays = LoanPolicy.DefaultLoanDays,
                UpdatedAtUtc = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc)
            });
        });
    }
}
