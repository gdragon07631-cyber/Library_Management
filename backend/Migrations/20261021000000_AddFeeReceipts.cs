using Microsoft.EntityFrameworkCore.Migrations;

namespace Project.Migrations;

public partial class AddFeeReceipts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("FeeReceipts", table => new
        {
            FeePaymentId = table.Column<long>(type: "bigint", nullable: false),
            ReaderName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
            CollectedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_FeeReceipts", r => r.FeePaymentId);
            table.ForeignKey("FK_FeeReceipts_FeePayments_FeePaymentId", r => r.FeePaymentId, "FeePayments", "Id", onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateTable("FeeReceiptLines", table => new
        {
            FeePaymentAllocationId = table.Column<long>(type: "bigint", nullable: false),
            FeePaymentId = table.Column<long>(type: "bigint", nullable: false),
            Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_FeeReceiptLines", r => r.FeePaymentAllocationId);
            table.ForeignKey("FK_FeeReceiptLines_FeePaymentAllocations_FeePaymentAllocationId", r => r.FeePaymentAllocationId, "FeePaymentAllocations", "Id", onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_FeeReceiptLines_FeeReceipts_FeePaymentId", r => r.FeePaymentId, "FeeReceipts", "FeePaymentId", onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateIndex("IX_FeeReceiptLines_FeePaymentId", "FeeReceiptLines", "FeePaymentId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("FeeReceiptLines");
        migrationBuilder.DropTable("FeeReceipts");
    }
}
