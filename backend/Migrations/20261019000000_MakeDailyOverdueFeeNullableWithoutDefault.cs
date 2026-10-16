using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Project.Data;

namespace Project.Migrations;

/// <summary>
/// PO xác nhận (S4-01): hệ thống không tự đặt mức phí trễ hạn mặc định.
/// Cột <c>LoanPolicies.DailyOverdueFee</c> chuyển sang cho phép NULL (= Quản lý chưa khai báo) và bỏ
/// giá trị mặc định 2.000 của cột. Không cập nhật dữ liệu: bản ghi chính sách hiện có giữ nguyên mức phí.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261019000000_MakeDailyOverdueFeeNullableWithoutDefault")]
public partial class MakeDailyOverdueFeeNullableWithoutDefault : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AlterColumn<decimal>(
            name: "DailyOverdueFee",
            table: "LoanPolicies",
            type: "decimal(18,2)",
            nullable: true,
            oldClrType: typeof(decimal),
            oldType: "decimal(18,2)",
            oldDefaultValue: 2_000m);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Lược đồ cũ bắt buộc có giá trị; chỉ khi rollback mới điền lại giá trị mặc định cũ cho bản ghi chưa khai báo.
        migrationBuilder.Sql("UPDATE LoanPolicies SET DailyOverdueFee = 2000 WHERE DailyOverdueFee IS NULL;");
        migrationBuilder.AlterColumn<decimal>(
            name: "DailyOverdueFee",
            table: "LoanPolicies",
            type: "decimal(18,2)",
            nullable: false,
            defaultValue: 2_000m,
            oldClrType: typeof(decimal),
            oldType: "decimal(18,2)",
            oldNullable: true);
    }
}

