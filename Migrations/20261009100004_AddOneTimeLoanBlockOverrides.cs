using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Migrations
{
    /// <inheritdoc />
    public partial class AddOneTimeLoanBlockOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActorRole",
                table: "AuditBlockLogs",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BookLoanId",
                table: "AuditBlockLogs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BlockingReasonCodes",
                table: "AuditBlockLogs",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureReason",
                table: "AuditBlockLogs",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EventType",
                table: "AuditBlockLogs",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "BlockedOther");

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "AuditBlockLogs",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Blocked");

            migrationBuilder.AddColumn<string>(
                name: "OverrideReason",
                table: "AuditBlockLogs",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OverrideRequestId",
                table: "AuditBlockLogs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditBlockLogs_EventType_OccurredAtUtc",
                table: "AuditBlockLogs",
                columns: new[] { "EventType", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditBlockLogs_ReaderAccountId_OccurredAtUtc",
                table: "AuditBlockLogs",
                columns: new[] { "ReaderAccountId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditBlockLogs_OverrideRequestId",
                table: "AuditBlockLogs",
                column: "OverrideRequestId",
                unique: true,
                filter: "[OverrideRequestId] IS NOT NULL");

            migrationBuilder.Sql("""
                UPDATE [AuditBlockLogs]
                SET [EventType] = CASE
                    WHEN [Outcome] = N'Used' THEN N'OverrideUsed'
                    WHEN [Outcome] = N'NotRequired' THEN N'OverrideNotRequired'
                    WHEN [Outcome] = N'Failed' THEN N'OverrideFailed'
                    WHEN (
                        CASE WHEN [Reasons] LIKE N'%Đang mượn % cuốn%' THEN 1 ELSE 0 END +
                        CASE WHEN [Reasons] LIKE N'%Thẻ thư viện đã hết hạn.%' THEN 1 ELSE 0 END +
                        CASE WHEN [Reasons] LIKE N'%Thẻ thư viện đang bị khóa.%' THEN 1 ELSE 0 END +
                        CASE WHEN [Reasons] LIKE N'%Bạn đọc có sách quá hạn chưa trả.%' THEN 1 ELSE 0 END +
                        CASE WHEN [Reasons] LIKE N'%Còn nợ % VND, không thể mượn sách.%' THEN 1 ELSE 0 END
                    ) > 1 THEN N'BlockedMultiple'
                    WHEN [Reasons] LIKE N'%Đang mượn % cuốn%' THEN N'BlockedLimit'
                    WHEN [Reasons] LIKE N'%Thẻ thư viện đã hết hạn.%' THEN N'BlockedCardExpired'
                    WHEN [Reasons] LIKE N'%Thẻ thư viện đang bị khóa.%' THEN N'BlockedCardLocked'
                    WHEN [Reasons] LIKE N'%Bạn đọc có sách quá hạn chưa trả.%' THEN N'BlockedOverdue'
                    WHEN [Reasons] LIKE N'%Còn nợ % VND, không thể mượn sách.%' THEN N'BlockedDebt'
                    ELSE N'BlockedOther'
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditBlockLogs_EventType_OccurredAtUtc",
                table: "AuditBlockLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditBlockLogs_ReaderAccountId_OccurredAtUtc",
                table: "AuditBlockLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditBlockLogs_OverrideRequestId",
                table: "AuditBlockLogs");

            migrationBuilder.DropColumn(
                name: "ActorRole",
                table: "AuditBlockLogs");

            migrationBuilder.DropColumn(
                name: "BookLoanId",
                table: "AuditBlockLogs");

            migrationBuilder.DropColumn(
                name: "BlockingReasonCodes",
                table: "AuditBlockLogs");

            migrationBuilder.DropColumn(
                name: "FailureReason",
                table: "AuditBlockLogs");

            migrationBuilder.DropColumn(
                name: "EventType",
                table: "AuditBlockLogs");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "AuditBlockLogs");

            migrationBuilder.DropColumn(
                name: "OverrideReason",
                table: "AuditBlockLogs");

            migrationBuilder.DropColumn(
                name: "OverrideRequestId",
                table: "AuditBlockLogs");
        }
    }
}
