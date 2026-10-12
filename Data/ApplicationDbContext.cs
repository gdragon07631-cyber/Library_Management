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
    public DbSet<Author> Authors => Set<Author>();
    public DbSet<Book> Books => Set<Book>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<LibraryCardType> LibraryCardTypes => Set<LibraryCardType>();
    public DbSet<LibraryCard> LibraryCards => Set<LibraryCard>();
    public DbSet<BookHold> BookHolds => Set<BookHold>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Shelf> Shelves => Set<Shelf>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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
            entity.Property(b => b.Isbn).HasMaxLength(50);
            entity.Property(b => b.Description).HasMaxLength(500);
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
            entity.Property(account => account.IsActive).HasDefaultValue(true);
            entity.Property(account => account.FailedLoginAttempts).HasDefaultValue(0);
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_AdminAccounts_FailedLoginAttempts_NonNegative", "[FailedLoginAttempts] >= 0"));
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
            entity.Property(reader => reader.StudentOrStaffCode).HasMaxLength(50).IsRequired();
            entity.Property(reader => reader.PasswordHash).HasMaxLength(512).IsRequired();
            entity.Property(reader => reader.Status).HasMaxLength(50).HasDefaultValue("Chờ duyệt").IsRequired();
            entity.Property(reader => reader.RejectionReason).HasMaxLength(1000);
            entity.Property(reader => reader.CreatedAtUtc).HasColumnType("datetime2");
            entity.Property(reader => reader.UpdatedAtUtc).HasColumnType("datetime2");
        });

        modelBuilder.Entity<LibraryCardType>(entity =>
        {
            entity.HasIndex(type => type.Name).IsUnique();
            entity.Property(type => type.Name).HasMaxLength(100).IsRequired();
            entity.Property(type => type.IsActive).HasDefaultValue(true);
        });

        modelBuilder.Entity<LibraryCard>(entity =>
        {
            entity.HasIndex(card => card.CardCode).IsUnique();
            entity.HasIndex(card => card.ReaderAccountId).IsUnique();
            entity.Property(card => card.CardCode).HasMaxLength(32).IsRequired();
            entity.Property(card => card.Status).HasMaxLength(50).HasDefaultValue("Đang hoạt động").IsRequired();
            entity.HasOne(card => card.ReaderAccount).WithOne(reader => reader.LibraryCard)
                .HasForeignKey<LibraryCard>(card => card.ReaderAccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(card => card.LibraryCardType).WithMany(type => type.LibraryCards)
                .HasForeignKey(card => card.LibraryCardTypeId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BookHold>(entity =>
        {
            entity.HasIndex(hold => new { hold.ReaderAccountId, hold.BookId }).IsUnique();
            entity.HasIndex(hold => new { hold.BookId, hold.Status, hold.HeldAtUtc, hold.Id });
            entity.HasIndex(hold => hold.BookCopyId).IsUnique().HasFilter("[BookCopyId] IS NOT NULL");
            entity.Property(hold => hold.HeldAtUtc).HasColumnType("datetime2");
            entity.Property(hold => hold.Status).HasMaxLength(50).HasDefaultValue(BookHoldStatuses.Pending).IsRequired();
            entity.HasOne(hold => hold.ReaderAccount).WithMany(reader => reader.BookHolds)
                .HasForeignKey(hold => hold.ReaderAccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(hold => hold.Book).WithMany()
                .HasForeignKey(hold => hold.BookId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(hold => hold.BookCopy).WithMany()
                .HasForeignKey(hold => hold.BookCopyId).OnDelete(DeleteBehavior.Restrict);
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
<<<<<<< HEAD
=======

        modelBuilder.Entity<BookLoan>(entity =>
        {
            entity.Property(loan => loan.LoanDate).HasColumnType("date");
            entity.Property(loan => loan.OriginalDueDate).HasColumnType("date");
            entity.Property(loan => loan.DueDate).HasColumnType("date");
            entity.Property(loan => loan.RenewalCount).HasDefaultValue(0);
            entity.Property(loan => loan.CreatedAtUtc).HasColumnType("datetime2");
            entity.Property(loan => loan.ReturnedAtUtc).HasColumnType("datetime2");
            entity.HasIndex(loan => loan.BookCopyId);
            entity.HasOne(loan => loan.Book).WithMany()
                .HasForeignKey(loan => loan.BookId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(loan => loan.BookCopy).WithMany()
                .HasForeignKey(loan => loan.BookCopyId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(loan => loan.ReaderAccount).WithMany()
                .HasForeignKey(loan => loan.ReaderAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BookCopy>(entity =>
        {
            entity.HasIndex(copy => copy.CopyCode).IsUnique();
            entity.HasIndex(copy => copy.ShelfId);
            entity.Property(copy => copy.CopyCode).HasMaxLength(50).IsRequired();
            entity.Property(copy => copy.Status).HasMaxLength(50).HasDefaultValue("Sẵn sàng").IsRequired();
            entity.HasOne(copy => copy.Book).WithMany()
                .HasForeignKey(copy => copy.BookId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(copy => copy.Shelf).WithMany()
                .HasForeignKey(copy => copy.ShelfId).OnDelete(DeleteBehavior.Restrict);
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

        modelBuilder.Entity<LoanPolicy>(entity =>
        {
            entity.Property(policy => policy.Id).ValueGeneratedNever();
            entity.Property(policy => policy.UpdatedAtUtc).HasColumnType("datetime2");
            entity.HasData(new LoanPolicy
            {
                Id = LoanPolicy.SingletonId,
                LoanDays = LoanPolicy.DefaultLoanDays,
                UpdatedAtUtc = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc)
            });
        });
>>>>>>> feature/S3-07/nhan_tra_nhieu_cuon_va_tong_ket
    }
}
