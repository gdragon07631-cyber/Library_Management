using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Migrations;

public partial class AddHoldPickupExpiryRuns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "HoldPickupExpiryRuns",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                CheckedCount = table.Column<int>(type: "int", nullable: false),
                CancelledCount = table.Column<int>(type: "int", nullable: false),
                AssignedToNextReaderCount = table.Column<int>(type: "int", nullable: false),
                ReturnedToAvailableCount = table.Column<int>(type: "int", nullable: false),
                ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                ResultMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_HoldPickupExpiryRuns", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_HoldPickupExpiryRuns_BusinessDate",
            table: "HoldPickupExpiryRuns",
            column: "BusinessDate",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_HoldPickupExpiryRuns_StartedAtUtc",
            table: "HoldPickupExpiryRuns",
            column: "StartedAtUtc");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "HoldPickupExpiryRuns");
}
