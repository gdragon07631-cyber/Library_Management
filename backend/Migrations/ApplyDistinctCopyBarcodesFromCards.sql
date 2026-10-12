IF OBJECT_ID(N'__EFMigrationsHistory', N'U') IS NULL
    THROW 51000, 'EF migration history was not found. Apply the existing database migrations first.', 1;
GO

IF NOT EXISTS (
    SELECT 1 FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261012010000_DistinguishCopyBarcodesFromCards'
)
BEGIN
    BEGIN TRY
        BEGIN TRANSACTION;

        UPDATE copy
        SET [CopyCode] = CONCAT('COPY-RENUMBERED-', REPLACE(CONVERT(varchar(36), NEWID()), '-', ''))
        FROM [BookCopies] AS copy
        WHERE EXISTS (
            SELECT 1
            FROM [LibraryCards] AS card
            WHERE card.[CardCode] = copy.[CopyCode]
        );

        INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
        VALUES (N'20261012010000_DistinguishCopyBarcodesFromCards', N'10.0.11');

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO
