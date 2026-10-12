using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Project.Data;

#nullable disable

namespace Project.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261012010000_DistinguishCopyBarcodesFromCards")]
public sealed class DistinguishCopyBarcodesFromCards : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE copy
            SET CopyCode = CONCAT('COPY-RENUMBERED-', REPLACE(CONVERT(varchar(36), NEWID()), '-', ''))
            FROM BookCopies AS copy
            WHERE EXISTS (
                SELECT 1
                FROM LibraryCards AS card
                WHERE card.CardCode = copy.CopyCode
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Restoring the old copy codes would reintroduce card/barcode collisions.
    }
}
