using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Project.Data;

#nullable disable

namespace Project.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261015180000_AddBookIncidents")]
public partial class AddBookIncidents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "BookIncidents",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                BookLoanId = table.Column<long>(type: "bigint", nullable: false),
                BookCopyId = table.Column<long>(type: "bigint", nullable: false),
                IncidentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                PhotoPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                ReportedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BookIncidents", x => x.Id);
                table.ForeignKey(
                    name: "FK_BookIncidents_BookLoans_BookLoanId",
                    column: x => x.BookLoanId,
                    principalTable: "BookLoans",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_BookIncidents_BookCopies_BookCopyId",
                    column: x => x.BookCopyId,
                    principalTable: "BookCopies",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_BookIncidents_BookLoanId",
            table: "BookIncidents",
            column: "BookLoanId");

        migrationBuilder.CreateIndex(
            name: "IX_BookIncidents_BookCopyId",
            table: "BookIncidents",
            column: "BookCopyId");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "BookIncidents");
}

