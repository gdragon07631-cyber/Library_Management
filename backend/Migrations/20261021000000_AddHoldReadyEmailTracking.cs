using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Project.Data;

namespace Project.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261021000000_AddHoldReadyEmailTracking")]
public sealed class AddHoldReadyEmailTracking : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(name: "HoldId", table: "EmailSendHistories", type: "bigint", nullable: true);
        migrationBuilder.CreateIndex(name: "IX_EmailSendHistories_HoldId", table: "EmailSendHistories", column: "HoldId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_EmailSendHistories_HoldId", table: "EmailSendHistories");
        migrationBuilder.DropColumn(name: "HoldId", table: "EmailSendHistories");
    }
}
