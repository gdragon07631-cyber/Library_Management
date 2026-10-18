using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Project.Data;

namespace Project.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261020000000_AddFeePayments")]
public partial class AddFeePayments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("FeePayments", table => new
        {
            Id = table.Column<long>(type: "bigint", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
            RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
            ReaderAccountId = table.Column<int>(type: "int", nullable: false),
            CollectedByAdminAccountId = table.Column<int>(type: "int", nullable: false),
            Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
            BalanceAfter = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
            PaidAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_FeePayments", p => p.Id);
            table.ForeignKey("FK_FeePayments_ReaderAccounts_ReaderAccountId", p => p.ReaderAccountId, "ReaderAccounts", "Id", onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_FeePayments_AdminAccounts_CollectedByAdminAccountId", p => p.CollectedByAdminAccountId, "AdminAccounts", "Id", onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateIndex("IX_FeePayments_RequestId", "FeePayments", "RequestId", unique: true);
        migrationBuilder.CreateIndex("IX_FeePayments_ReaderAccountId", "FeePayments", "ReaderAccountId");
        migrationBuilder.CreateIndex("IX_FeePayments_CollectedByAdminAccountId", "FeePayments", "CollectedByAdminAccountId");
        migrationBuilder.CreateTable("FeePaymentAllocations", table => new
        {
            Id = table.Column<long>(type: "bigint", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
            FeePaymentId = table.Column<long>(type: "bigint", nullable: false),
            ReaderFeeId = table.Column<int>(type: "int", nullable: false),
            Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_FeePaymentAllocations", a => a.Id);
            table.ForeignKey("FK_FeePaymentAllocations_FeePayments_FeePaymentId", a => a.FeePaymentId, "FeePayments", "Id", onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_FeePaymentAllocations_ReaderFees_ReaderFeeId", a => a.ReaderFeeId, "ReaderFees", "Id", onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateIndex("IX_FeePaymentAllocations_FeePaymentId_ReaderFeeId", "FeePaymentAllocations", new[] { "FeePaymentId", "ReaderFeeId" }, unique: true);
        migrationBuilder.CreateIndex("IX_FeePaymentAllocations_ReaderFeeId", "FeePaymentAllocations", "ReaderFeeId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("FeePaymentAllocations");
        migrationBuilder.DropTable("FeePayments");
    }
}
