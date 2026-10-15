using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Project.Data;

namespace Project.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261015200000_AddBookLoanCloseReasonAndOverdueFee")]
public partial class AddBookLoanCloseReasonAndOverdueFee : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "CloseReason",
            table: "BookLoans",
            type: "nvarchar(50)",
            maxLength: 50,
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "OverdueFee",
            table: "BookLoans",
            type: "decimal(18,2)",
            nullable: false,
            defaultValue: 0m);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CloseReason",
            table: "BookLoans");

        migrationBuilder.DropColumn(
            name: "OverdueFee",
            table: "BookLoans");
    }
}

