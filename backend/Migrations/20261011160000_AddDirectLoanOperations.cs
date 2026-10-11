using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace Project.Migrations;

public partial class AddDirectLoanOperations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(name: "DirectLoanOperations", columns: table => new
        {
            RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
            StaffId = table.Column<int>(type: "int", nullable: false),
            PayloadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
            CardCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
            CopyCodesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
            RowIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
            CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
            CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_DirectLoanOperations", x => x.RequestId);
            table.ForeignKey("FK_DirectLoanOperations_AdminAccounts_StaffId", x => x.StaffId, "AdminAccounts", "Id", onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateTable(name: "DirectLoanOperationItems", columns: table => new
        {
            RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
            Position = table.Column<int>(type: "int", nullable: false),
            BookLoanId = table.Column<long>(type: "bigint", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_DirectLoanOperationItems", x => new { x.RequestId, x.Position });
            table.ForeignKey("FK_DirectLoanOperationItems_DirectLoanOperations_RequestId", x => x.RequestId, "DirectLoanOperations", "RequestId", onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_DirectLoanOperationItems_BookLoans_BookLoanId", x => x.BookLoanId, "BookLoans", "Id", onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateIndex("IX_DirectLoanOperations_StaffId", "DirectLoanOperations", "StaffId");
        migrationBuilder.CreateIndex("IX_DirectLoanOperationItems_BookLoanId", "DirectLoanOperationItems", "BookLoanId", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("DirectLoanOperationItems");
        migrationBuilder.DropTable("DirectLoanOperations");
    }
}
