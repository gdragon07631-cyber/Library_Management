IF OBJECT_ID(N'__EFMigrationsHistory', N'U') IS NULL
    THROW 51000, 'EF migration history was not found. Apply the existing database migrations first.', 1;
GO

IF NOT EXISTS (
    SELECT 1 FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261012020000_ShortCopyBarcodesForRenumberedCopies'
)
BEGIN
    BEGIN TRY
        BEGIN TRANSACTION;

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

        INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
        VALUES (N'20261012020000_ShortCopyBarcodesForRenumberedCopies', N'10.0.11');

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO
