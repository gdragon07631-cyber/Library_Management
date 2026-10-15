using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Project.Data;

namespace Project.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261017000000_AddReturnOverdueReceivable")]
public partial class AddReturnOverdueReceivable : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "OverdueDaysAtReturn", table: "BookLoans", type: "int", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<decimal>(
            name: "DailyOverdueFeeAtReturn", table: "BookLoans", type: "decimal(18,2)", nullable: false, defaultValue: 0m);

        migrationBuilder.CreateTable(
            name: "ReaderFees",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                ReaderAccountId = table.Column<int>(type: "int", nullable: false),
                BookLoanId = table.Column<long>(type: "bigint", nullable: true),
                Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                PaidAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false, defaultValue: 0m),
                IsPaid = table.Column<bool>(type: "bit", nullable: false),
                Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ReaderFees", x => x.Id);
                table.ForeignKey("FK_ReaderFees_BookLoans_BookLoanId", x => x.BookLoanId, "BookLoans", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_ReaderFees_ReaderAccounts_ReaderAccountId", x => x.ReaderAccountId, "ReaderAccounts", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(name: "IX_ReaderFees_ReaderAccountId", table: "ReaderFees", column: "ReaderAccountId");
        migrationBuilder.CreateIndex(name: "IX_ReaderFees_BookLoanId", table: "ReaderFees", column: "BookLoanId", unique: true, filter: "[BookLoanId] IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ReaderFees");
        migrationBuilder.DropColumn(name: "DailyOverdueFeeAtReturn", table: "BookLoans");
        migrationBuilder.DropColumn(name: "OverdueDaysAtReturn", table: "BookLoans");
    }
}
