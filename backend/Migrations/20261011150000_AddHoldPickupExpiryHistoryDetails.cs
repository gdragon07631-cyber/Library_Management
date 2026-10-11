using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Migrations;

public partial class AddHoldPickupExpiryHistoryDetails : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "ExpiredBookCopyId", table: "BookHolds", type: "bigint", nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "ExpiredBookCopyCode", table: "BookHolds", type: "nvarchar(50)", maxLength: 50, nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "ExpiryCopyOutcome", table: "BookHolds", type: "nvarchar(30)", maxLength: 30, nullable: true);
        migrationBuilder.AddColumn<long>(
            name: "ExpiryNextHoldId", table: "BookHolds", type: "bigint", nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_BookHolds_ExpiredBookCopyId",
            table: "BookHolds",
            column: "ExpiredBookCopyId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_BookHolds_ExpiredBookCopyId", table: "BookHolds");
        migrationBuilder.DropColumn(name: "ExpiredBookCopyId", table: "BookHolds");
        migrationBuilder.DropColumn(name: "ExpiredBookCopyCode", table: "BookHolds");
        migrationBuilder.DropColumn(name: "ExpiryCopyOutcome", table: "BookHolds");
        migrationBuilder.DropColumn(name: "ExpiryNextHoldId", table: "BookHolds");
    }
}
