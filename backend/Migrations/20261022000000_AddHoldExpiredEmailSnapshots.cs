using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Project.Data;

namespace Project.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261022000000_AddHoldExpiredEmailSnapshots")]
public sealed class AddHoldExpiredEmailSnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "BookTitleSnapshot", table: "EmailSendHistories", type: "nvarchar(250)", maxLength: 250, nullable: true);
        migrationBuilder.AddColumn<DateTime>(name: "PickupDeadlineUtcSnapshot", table: "EmailSendHistories", type: "datetime2", nullable: true);
        migrationBuilder.AddColumn<DateTime>(name: "EventOccurredAtUtc", table: "EmailSendHistories", type: "datetime2", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "BookTitleSnapshot", table: "EmailSendHistories");
        migrationBuilder.DropColumn(name: "PickupDeadlineUtcSnapshot", table: "EmailSendHistories");
        migrationBuilder.DropColumn(name: "EventOccurredAtUtc", table: "EmailSendHistories");
    }
}
