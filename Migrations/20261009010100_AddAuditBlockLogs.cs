using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Project.Data;

#nullable disable

namespace Project.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20261009010100_AddAuditBlockLogs")]
    public partial class AddAuditBlockLogs : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditBlockLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReaderAccountId = table.Column<int>(type: "int", nullable: false),
                    ReaderCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ReaderName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BorrowedCount = table.Column<int>(type: "int", nullable: false),
                    BorrowingLimit = table.Column<int>(type: "int", nullable: false),
                    OverdueLoanCount = table.Column<int>(type: "int", nullable: false),
                    CardStatus = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CardExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Reasons = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditBlockLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditBlockLogs_ReaderAccountId",
                table: "AuditBlockLogs",
                column: "ReaderAccountId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditBlockLogs");
        }
    }
}
