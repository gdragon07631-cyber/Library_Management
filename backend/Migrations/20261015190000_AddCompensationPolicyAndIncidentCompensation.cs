using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Project.Data;

#nullable disable

namespace Project.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261015190000_AddCompensationPolicyAndIncidentCompensation")]
public partial class AddCompensationPolicyAndIncidentCompensation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CompensationPolicies",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                DefaultRate = table.Column<decimal>(type: "decimal(6,2)", nullable: false, defaultValue: 1.0m),
                LostRate = table.Column<decimal>(type: "decimal(6,2)", nullable: true),
                DamagedRate = table.Column<decimal>(type: "decimal(6,2)", nullable: true),
                UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CompensationPolicies", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "BookIncidentCompensations",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                BookIncidentId = table.Column<long>(type: "bigint", nullable: false),
                RecordedCoverPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                AppliedRate = table.Column<decimal>(type: "decimal(6,2)", nullable: true),
                InitialAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                CurrentAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                IsAdjusted = table.Column<bool>(type: "bit", nullable: false),
                AdjustmentReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                AdjustedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                AdjustedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BookIncidentCompensations", x => x.Id);
                table.ForeignKey(
                    name: "FK_BookIncidentCompensations_BookIncidents_BookIncidentId",
                    column: x => x.BookIncidentId,
                    principalTable: "BookIncidents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "CompensationAdjustmentHistories",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                BookIncidentCompensationId = table.Column<long>(type: "bigint", nullable: false),
                FromAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                ToAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                AdjustedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                AdjustedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CompensationAdjustmentHistories", x => x.Id);
                table.ForeignKey(
                    name: "FK_CompensationAdjustmentHistories_BookIncidentCompensations_BookIncidentCompensationId",
                    column: x => x.BookIncidentCompensationId,
                    principalTable: "BookIncidentCompensations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_BookIncidentCompensations_BookIncidentId",
            table: "BookIncidentCompensations",
            column: "BookIncidentId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CompensationAdjustmentHistories_BookIncidentCompensationId_AdjustedAtUtc",
            table: "CompensationAdjustmentHistories",
            columns: new[] { "BookIncidentCompensationId", "AdjustedAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "CompensationAdjustmentHistories");
        migrationBuilder.DropTable(name: "BookIncidentCompensations");
        migrationBuilder.DropTable(name: "CompensationPolicies");
    }
}

