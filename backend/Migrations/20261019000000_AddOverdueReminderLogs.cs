using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Project.Data;

namespace Project.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261019000000_AddOverdueReminderLogs")]
public partial class AddOverdueReminderLogs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OverdueReminderLogs",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                BookLoanId = table.Column<long>(type: "bigint", nullable: false),
                MilestoneDays = table.Column<int>(type: "int", nullable: false),
                ReaderAccountId = table.Column<int>(type: "int", nullable: false),
                BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                SentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                ErrorMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_OverdueReminderLogs", x => x.Id));
        migrationBuilder.CreateIndex("IX_OverdueReminderLogs_BookLoanId_MilestoneDays", "OverdueReminderLogs",
            new[] { "BookLoanId", "MilestoneDays" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("OverdueReminderLogs");
}
