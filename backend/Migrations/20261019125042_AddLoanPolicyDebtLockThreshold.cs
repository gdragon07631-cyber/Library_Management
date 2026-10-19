using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Project.Data;

#nullable disable

namespace Project.Migrations
{
    /// <summary>Lưu ngưỡng nợ phí đề xuất khoá thẻ (mặc định 500.000 đ) cho các bản ghi chính sách mượn hiện có.</summary>
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20261019125042_AddLoanPolicyDebtLockThreshold")]
    public partial class AddLoanPolicyDebtLockThreshold : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DebtLockThreshold",
                table: "LoanPolicies",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 500000m);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DebtLockThreshold",
                table: "LoanPolicies");
        }
    }
}
