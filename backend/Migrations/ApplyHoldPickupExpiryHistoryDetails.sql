BEGIN TRANSACTION;
ALTER TABLE [BookHolds] ADD [ExpiredBookCopyId] bigint NULL;

ALTER TABLE [BookHolds] ADD [ExpiredBookCopyCode] nvarchar(50) NULL;

ALTER TABLE [BookHolds] ADD [ExpiryCopyOutcome] nvarchar(30) NULL;

ALTER TABLE [BookHolds] ADD [ExpiryNextHoldId] bigint NULL;

CREATE INDEX [IX_BookHolds_ExpiredBookCopyId] ON [BookHolds] ([ExpiredBookCopyId]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261011150000_AddHoldPickupExpiryHistoryDetails', N'10.0.11');

COMMIT;
GO

