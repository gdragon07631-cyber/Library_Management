using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Migrations
{
    /// <inheritdoc />
    public partial class AddIncidentAdjustmentVouchers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsReversed",
                table: "BookIncidents",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "ReversalVoucherId",
                table: "BookIncidents",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IncidentAdjustmentVouchers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VoucherCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    BookIncidentId = table.Column<long>(type: "bigint", nullable: false),
                    BookLoanId = table.Column<long>(type: "bigint", nullable: false),
                    BookCopyId = table.Column<long>(type: "bigint", nullable: false),
                    AdjustmentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ReferenceDocument = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ApprovedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PreviousCopyStatus = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PreviousLoanCloseReason = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PreviousCompensationAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PreviousOverdueFee = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    NewCopyStatus = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    NewLoanCloseReason = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    NewCompensationAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    NewOverdueFee = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentAdjustmentVouchers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentAdjustmentVouchers_BookCopies_BookCopyId",
                        column: x => x.BookCopyId,
                        principalTable: "BookCopies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IncidentAdjustmentVouchers_BookIncidents_BookIncidentId",
                        column: x => x.BookIncidentId,
                        principalTable: "BookIncidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IncidentAdjustmentVouchers_BookLoans_BookLoanId",
                        column: x => x.BookLoanId,
                        principalTable: "BookLoans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentAdjustmentVouchers_BookCopyId",
                table: "IncidentAdjustmentVouchers",
                column: "BookCopyId");

            migrationBuilder.CreateIndex(
                name: "IX_IncidentAdjustmentVouchers_BookIncidentId",
                table: "IncidentAdjustmentVouchers",
                column: "BookIncidentId");

            migrationBuilder.CreateIndex(
                name: "IX_IncidentAdjustmentVouchers_BookLoanId_CreatedAtUtc",
                table: "IncidentAdjustmentVouchers",
                columns: new[] { "BookLoanId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentAdjustmentVouchers_VoucherCode",
                table: "IncidentAdjustmentVouchers",
                column: "VoucherCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IncidentAdjustmentVouchers");

            migrationBuilder.DropColumn(
                name: "IsReversed",
                table: "BookIncidents");

            migrationBuilder.DropColumn(
                name: "ReversalVoucherId",
                table: "BookIncidents");
        }
    }
}

