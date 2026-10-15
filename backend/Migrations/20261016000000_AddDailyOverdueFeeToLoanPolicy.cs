using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Project.Data;

namespace Project.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261016000000_AddDailyOverdueFeeToLoanPolicy")]
public partial class AddDailyOverdueFeeToLoanPolicy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<decimal>(
        name: "DailyOverdueFee", table: "LoanPolicies", type: "decimal(18,2)", nullable: false, defaultValue: 2_000m);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(
        name: "DailyOverdueFee", table: "LoanPolicies");
}
