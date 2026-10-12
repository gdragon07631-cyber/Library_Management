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
                name: "IX_BookHolds_BookCopyId",
                table: "BookHolds");

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BookHolds_BookCopyId",
                table: "BookHolds");

            migrationBuilder.DropIndex(
                name: "IX_BookHolds_BookId_Status_HeldAtUtc_Id",
                table: "BookHolds");

            migrationBuilder.CreateIndex(
                name: "IX_BookHolds_BookCopyId",
                table: "BookHolds",
                column: "BookCopyId");
        }
    }
}
