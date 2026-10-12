using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Migrations
{
    /// <inheritdoc />
    public partial class AddBookHoldAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BookHolds_BookId",
                table: "BookHolds");

            migrationBuilder.AddColumn<long>(
                name: "BookCopyId",
                table: "BookHolds",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "BookHolds",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Đang chờ");

            migrationBuilder.CreateIndex(
                name: "IX_BookHolds_BookCopyId",
                table: "BookHolds",
                column: "BookCopyId",
                unique: true,
                filter: "[BookCopyId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BookHolds_BookId_Status_HeldAtUtc_Id",
                table: "BookHolds",
                columns: new[] { "BookId", "Status", "HeldAtUtc", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_BookHolds_BookCopies_BookCopyId",
                table: "BookHolds",
                column: "BookCopyId",
                principalTable: "BookCopies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BookHolds_BookCopies_BookCopyId",
                table: "BookHolds");

            migrationBuilder.DropIndex(
                name: "IX_BookHolds_BookCopyId",
                table: "BookHolds");

            migrationBuilder.DropIndex(
                name: "IX_BookHolds_BookId_Status_HeldAtUtc_Id",
                table: "BookHolds");

            migrationBuilder.DropColumn(
                name: "BookCopyId",
                table: "BookHolds");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "BookHolds");

            migrationBuilder.CreateIndex(
                name: "IX_BookHolds_BookId",
                table: "BookHolds",
                column: "BookId");
        }
    }
}
