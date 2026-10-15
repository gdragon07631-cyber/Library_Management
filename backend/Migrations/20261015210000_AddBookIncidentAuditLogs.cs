using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Migrations
{
    /// <inheritdoc />
    public partial class AddBookIncidentAuditLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BookIncidentAuditLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BookLoanId = table.Column<long>(type: "bigint", nullable: false),
                    BookIncidentId = table.Column<long>(type: "bigint", nullable: true),
                    BookCopyId = table.Column<long>(type: "bigint", nullable: true),
                    OperationType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IncidentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PhotoPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CopyStatusResult = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CloseReason = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    IncidentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    OverdueFee = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    RecordedCoverPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    AppliedRate = table.Column<decimal>(type: "decimal(6,2)", nullable: true),
                    InitialCompensationAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    FromAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    ToAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    AdjustmentReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookIncidentAuditLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookIncidentAuditLogs_BookCopies_BookCopyId",
                        column: x => x.BookCopyId,
                        principalTable: "BookCopies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookIncidentAuditLogs_BookIncidents_BookIncidentId",
                        column: x => x.BookIncidentId,
                        principalTable: "BookIncidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookIncidentAuditLogs_BookLoans_BookLoanId",
                        column: x => x.BookLoanId,
                        principalTable: "BookLoans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookIncidentAuditLogs_BookCopyId",
                table: "BookIncidentAuditLogs",
                column: "BookCopyId");

            migrationBuilder.CreateIndex(
                name: "IX_BookIncidentAuditLogs_BookIncidentId",
                table: "BookIncidentAuditLogs",
                column: "BookIncidentId");

            migrationBuilder.CreateIndex(
                name: "IX_BookIncidentAuditLogs_BookLoanId_OccurredAtUtc",
                table: "BookIncidentAuditLogs",
                columns: new[] { "BookLoanId", "OccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookIncidentAuditLogs");
        }
    }
}

