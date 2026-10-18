using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Project.Data;

namespace Project.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261020000000_AddEmailSendHistories")]
public partial class AddEmailSendHistories : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "EmailSendHistories",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                DeduplicationKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ReaderAccountId = table.Column<int>(type: "int", nullable: true),
                Recipient = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                NotificationType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                Attempts = table.Column<int>(type: "int", nullable: false),
                AttemptErrors = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                ErrorMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                OverdueMilestoneDays = table.Column<int>(type: "int", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_EmailSendHistories", x => x.Id));
        migrationBuilder.CreateIndex("IX_EmailSendHistories_StartedAtUtc", "EmailSendHistories", "StartedAtUtc");
        migrationBuilder.CreateIndex("IX_EmailSendHistories_DeduplicationKey", "EmailSendHistories", "DeduplicationKey", unique: true);
        migrationBuilder.CreateIndex("IX_EmailSendHistories_ReaderAccountId_StartedAtUtc", "EmailSendHistories", new[] { "ReaderAccountId", "StartedAtUtc" });
        migrationBuilder.CreateIndex("IX_EmailSendHistories_Recipient_StartedAtUtc", "EmailSendHistories", new[] { "Recipient", "StartedAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("EmailSendHistories");
}
