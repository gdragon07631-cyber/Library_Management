using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Project.Data;

namespace Project.Migrations;

public partial class AddFeeWaiverRequests : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "WaivedAmount", table: "ReaderFees", type: "decimal(18,2)", nullable: false, defaultValue: 0m);

        migrationBuilder.CreateTable(
            name: "FeeWaiverRequests",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                ReaderFeeId = table.Column<int>(type: "int", nullable: false),
                OriginalFeeAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                FeeBalanceAtRequest = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                ReaderBalanceAtRequest = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                RequestedAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                RequestedByAdminAccountId = table.Column<int>(type: "int", nullable: false),
                RequestedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                ReviewedByAdminAccountId = table.Column<int>(type: "int", nullable: true),
                ReviewedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                ReviewNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FeeWaiverRequests", row => row.Id);
                table.ForeignKey("FK_FeeWaiverRequests_ReaderFees_ReaderFeeId", row => row.ReaderFeeId,
                    "ReaderFees", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_FeeWaiverRequests_AdminAccounts_RequestedByAdminAccountId", row => row.RequestedByAdminAccountId,
                    "AdminAccounts", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_FeeWaiverRequests_AdminAccounts_ReviewedByAdminAccountId", row => row.ReviewedByAdminAccountId,
                    "AdminAccounts", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("IX_FeeWaiverRequests_ReaderFeeId_Status", "FeeWaiverRequests", new[] { "ReaderFeeId", "Status" });
        migrationBuilder.CreateIndex("IX_FeeWaiverRequests_RequestedByAdminAccountId", "FeeWaiverRequests", "RequestedByAdminAccountId");
        migrationBuilder.CreateIndex("IX_FeeWaiverRequests_ReviewedByAdminAccountId", "FeeWaiverRequests", "ReviewedByAdminAccountId");
        migrationBuilder.CreateIndex("IX_FeeWaiverRequests_Status_RequestedAtUtc", "FeeWaiverRequests", new[] { "Status", "RequestedAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("FeeWaiverRequests");
        migrationBuilder.DropColumn("WaivedAmount", "ReaderFees");
    }
}
