using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Project.Data;

#nullable disable

namespace Project.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20261016000000_AddLibraryCardLockMetadata")]
    public partial class AddLibraryCardLockMetadata : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LockReason",
                table: "LibraryCards",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LockedAtUtc",
                table: "LibraryCards",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LockExpiresAtUtc",
                table: "LibraryCards",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsLockUntilManualUnlock",
                table: "LibraryCards",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "LockedByAdminAccountId",
                table: "LibraryCards",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LibraryCards_LockedByAdminAccountId",
                table: "LibraryCards",
                column: "LockedByAdminAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_LibraryCards_AdminAccounts_LockedByAdminAccountId",
                table: "LibraryCards",
                column: "LockedByAdminAccountId",
                principalTable: "AdminAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LibraryCards_AdminAccounts_LockedByAdminAccountId",
                table: "LibraryCards");

            migrationBuilder.DropIndex(
                name: "IX_LibraryCards_LockedByAdminAccountId",
                table: "LibraryCards");

            migrationBuilder.DropColumn(
                name: "LockReason",
                table: "LibraryCards");

            migrationBuilder.DropColumn(
                name: "LockedAtUtc",
                table: "LibraryCards");

            migrationBuilder.DropColumn(
                name: "LockExpiresAtUtc",
                table: "LibraryCards");

            migrationBuilder.DropColumn(
                name: "IsLockUntilManualUnlock",
                table: "LibraryCards");

            migrationBuilder.DropColumn(
                name: "LockedByAdminAccountId",
                table: "LibraryCards");
        }
    }
}
