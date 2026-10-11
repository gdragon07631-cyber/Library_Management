using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Project.Data;

#nullable disable

namespace Project.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261009000000_AddBorrowingLimitAndLoanReturnTracking")]
public sealed class AddBorrowingLimitAndLoanReturnTracking : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "MaxBorrowedBooks",
            table: "LibraryCardTypes",
            type: "int",
            nullable: false,
            defaultValue: 100);

        migrationBuilder.AddColumn<DateTime>(
            name: "ReturnedAtUtc",
            table: "BookLoans",
            type: "datetime2",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "MaxBorrowedBooks", table: "LibraryCardTypes");
        migrationBuilder.DropColumn(name: "ReturnedAtUtc", table: "BookLoans");
    }
}
