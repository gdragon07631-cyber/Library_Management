using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Migrations
{
    /// <inheritdoc />
    public partial class AddBookCopyLoanLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BookCopyId",
                table: "BookLoans",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReturnedAtUtc",
                table: "BookLoans",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookLoans_BookCopyId",
                table: "BookLoans",
                column: "BookCopyId");

            migrationBuilder.AddForeignKey(
                name: "FK_BookLoans_BookCopies_BookCopyId",
                table: "BookLoans",
                column: "BookCopyId",
                principalTable: "BookCopies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BookLoans_BookCopies_BookCopyId",
                table: "BookLoans");

            migrationBuilder.DropIndex(
                name: "IX_BookLoans_BookCopyId",
                table: "BookLoans");

            migrationBuilder.DropColumn(
                name: "BookCopyId",
                table: "BookLoans");

            migrationBuilder.DropColumn(
                name: "ReturnedAtUtc",
                table: "BookLoans");
        }
    }
}
