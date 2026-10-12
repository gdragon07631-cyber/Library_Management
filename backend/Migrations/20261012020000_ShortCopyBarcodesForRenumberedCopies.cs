using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Project.Data;

#nullable disable

namespace Project.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261012020000_ShortCopyBarcodesForRenumberedCopies")]
public sealed class ShortCopyBarcodesForRenumberedCopies : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DECLARE @maxNumber int;
            DECLARE @renumberCount int;

            SELECT @maxNumber = ISNULL(MAX(numbered.Number), 0)
            FROM (
                SELECT TRY_CONVERT(int, SUBSTRING([CopyCode], 4, 6)) AS Number
                FROM [BookCopies]
                WHERE LEN([CopyCode]) = 9 AND [CopyCode] LIKE 'CPY[0-9][0-9][0-9][0-9][0-9][0-9]'
                UNION ALL
                SELECT TRY_CONVERT(int, SUBSTRING([CardCode], 4, 6)) AS Number
                FROM [LibraryCards]
                WHERE LEN([CardCode]) = 9 AND [CardCode] LIKE 'CPY[0-9][0-9][0-9][0-9][0-9][0-9]'
            ) AS numbered;

            SELECT @renumberCount = COUNT(*)
            FROM [BookCopies]
            WHERE [CopyCode] LIKE 'COPY-RENUMBERED-%';

            IF @maxNumber + @renumberCount > 999999
                THROW 51000, 'The CPY barcode sequence has no room for all renumbered copies.', 1;

            ;WITH RenumberedCopies AS (
                SELECT [Id], ROW_NUMBER() OVER (ORDER BY [Id]) AS SequenceNumber
                FROM [BookCopies]
                WHERE [CopyCode] LIKE 'COPY-RENUMBERED-%'
            )
            UPDATE copy
            SET [CopyCode] = CONCAT('CPY', RIGHT('000000' + CONVERT(varchar(6), @maxNumber + renumbered.SequenceNumber), 6))
            FROM [BookCopies] AS copy
            INNER JOIN RenumberedCopies AS renumbered ON renumbered.[Id] = copy.[Id];
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Keep the short, scannable identifiers when rolling back other migrations.
    }
}
