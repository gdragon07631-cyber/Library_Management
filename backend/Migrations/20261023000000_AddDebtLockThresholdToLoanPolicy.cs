using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Project.Data;

#nullable disable

namespace Project.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20261023000000_AddDebtLockThresholdToLoanPolicy")]
    public partial class AddDebtLockThresholdToLoanPolicy : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('dbo.LoanPolicies', 'DebtLockThreshold') IS NULL
BEGIN
    ALTER TABLE [LoanPolicies] ADD [DebtLockThreshold] decimal(18,2) NOT NULL CONSTRAINT [DF_LoanPolicies_DebtLockThreshold] DEFAULT 500000.0;
END
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('dbo.LoanPolicies', 'DebtLockThreshold') IS NOT NULL
BEGIN
    DECLARE @ConstraintName nvarchar(200);
    SELECT @ConstraintName = dc.name
    FROM sys.default_constraints dc
    INNER JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
    WHERE dc.parent_object_id = OBJECT_ID('dbo.LoanPolicies') AND c.name = 'DebtLockThreshold';
    IF @ConstraintName IS NOT NULL
        EXEC('ALTER TABLE [LoanPolicies] DROP CONSTRAINT [' + @ConstraintName + ']');
    ALTER TABLE [LoanPolicies] DROP COLUMN [DebtLockThreshold];
END
");
        }
    }
}

