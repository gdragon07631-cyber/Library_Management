using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Project.Data;

#nullable disable

namespace Project.Migrations
{
    /// <summary>
    /// S4-04 lát 4: nhật ký khoá/mở khoá thẻ chỉ được thêm mới. Các thẻ đang bị khoá trước khi có bảng này
    /// được ghi một bản ghi "Khoá thẻ" từ dữ liệu khoá hiện có để lịch sử không bị thiếu.
    /// </summary>
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20261019134553_AddLibraryCardLockHistories")]
    public partial class AddLibraryCardLockHistories : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LibraryCardLockHistories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LibraryCardId = table.Column<int>(type: "int", nullable: false),
                    CardCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReaderAccountId = table.Column<int>(type: "int", nullable: false),
                    ReaderName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PerformedByAdminAccountId = table.Column<int>(type: "int", nullable: true),
                    PerformedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    LockReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    UnlockReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LockedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LockExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsLockUntilManualUnlock = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryCardLockHistories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryCardLockHistories_CardCode",
                table: "LibraryCardLockHistories",
                column: "CardCode");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryCardLockHistories_OccurredAtUtc",
                table: "LibraryCardLockHistories",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryCardLockHistories_ReaderAccountId_OccurredAtUtc",
                table: "LibraryCardLockHistories",
                columns: new[] { "ReaderAccountId", "OccurredAtUtc" });

            // Giữ lại các lần khoá đang có hiệu lực (khoá thủ công, khoá có thời hạn hoặc dữ liệu cũ chỉ có trạng thái "khoá").
            migrationBuilder.Sql("""
                INSERT INTO [LibraryCardLockHistories]
                    ([LibraryCardId], [CardCode], [ReaderAccountId], [ReaderName], [EventType], [OccurredAtUtc],
                     [PerformedByAdminAccountId], [PerformedBy], [LockReason], [UnlockReason],
                     [LockedAtUtc], [LockExpiresAtUtc], [IsLockUntilManualUnlock])
                SELECT c.[Id], c.[CardCode], c.[ReaderAccountId], r.[FullName], N'Khoá thẻ',
                       COALESCE(c.[LockedAtUtc], SYSUTCDATETIME()),
                       c.[LockedByAdminAccountId], COALESCE(a.[Email], N'Không rõ (dữ liệu trước khi có nhật ký)'),
                       c.[LockReason], NULL, c.[LockedAtUtc], c.[LockExpiresAtUtc], c.[IsLockUntilManualUnlock]
                FROM [LibraryCards] c
                INNER JOIN [ReaderAccounts] r ON r.[Id] = c.[ReaderAccountId]
                LEFT JOIN [AdminAccounts] a ON a.[Id] = c.[LockedByAdminAccountId]
                WHERE (c.[LockedAtUtc] IS NOT NULL
                       AND (c.[IsLockUntilManualUnlock] = 1 OR c.[LockExpiresAtUtc] IS NULL OR c.[LockExpiresAtUtc] > SYSUTCDATETIME()))
                   OR (c.[LockedAtUtc] IS NULL AND (c.[Status] LIKE N'%khoá%' OR c.[Status] LIKE N'%khóa%'));
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER [TR_LibraryCardLockHistories_ReadOnly] ON [LibraryCardLockHistories]
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, N'Nhật ký khoá/mở khoá thẻ là dữ liệu chỉ đọc, không được sửa hoặc xóa.', 1;
                END
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [TR_LibraryCardLockHistories_ReadOnly];");
            migrationBuilder.DropTable(
                name: "LibraryCardLockHistories");
        }
    }
}
