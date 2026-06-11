using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentHub.API.Migrations
{
    [Migration("20260610170000_AddRentPaymentReceipts")]
    public partial class AddRentPaymentReceipts : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('Payments', 'ProviderReceiptUrl') IS NULL
BEGIN
    ALTER TABLE [Payments] ADD [ProviderReceiptUrl] nvarchar(2048) NULL;
END

IF COL_LENGTH('Payments', 'SystemReceiptNumber') IS NULL
BEGIN
    ALTER TABLE [Payments] ADD [SystemReceiptNumber] nvarchar(64) NULL;
END

IF COL_LENGTH('Payments', 'ReceiptVerificationCode') IS NULL
BEGIN
    ALTER TABLE [Payments] ADD [ReceiptVerificationCode] nvarchar(64) NULL;
END

IF COL_LENGTH('Payments', 'ReceiptIssuedAt') IS NULL
BEGIN
    ALTER TABLE [Payments] ADD [ReceiptIssuedAt] datetimeoffset NULL;
END
");

            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_Payments_SystemReceiptNumber' AND [object_id] = OBJECT_ID(N'[Payments]'))
BEGIN
    CREATE UNIQUE INDEX [IX_Payments_SystemReceiptNumber]
    ON [Payments]([SystemReceiptNumber])
    WHERE [SystemReceiptNumber] IS NOT NULL AND [SystemReceiptNumber] <> N'' AND [IsDeleted] = 0;
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_Payments_ReceiptVerificationCode' AND [object_id] = OBJECT_ID(N'[Payments]'))
BEGIN
    CREATE UNIQUE INDEX [IX_Payments_ReceiptVerificationCode]
    ON [Payments]([ReceiptVerificationCode])
    WHERE [ReceiptVerificationCode] IS NOT NULL AND [ReceiptVerificationCode] <> N'' AND [IsDeleted] = 0;
END
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_Payments_SystemReceiptNumber' AND [object_id] = OBJECT_ID(N'[Payments]'))
BEGIN
    DROP INDEX [IX_Payments_SystemReceiptNumber] ON [Payments];
END

IF EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_Payments_ReceiptVerificationCode' AND [object_id] = OBJECT_ID(N'[Payments]'))
BEGIN
    DROP INDEX [IX_Payments_ReceiptVerificationCode] ON [Payments];
END
");

            DropColumnIfExists(migrationBuilder, "Payments", "ReceiptIssuedAt");
            DropColumnIfExists(migrationBuilder, "Payments", "ReceiptVerificationCode");
            DropColumnIfExists(migrationBuilder, "Payments", "SystemReceiptNumber");
            DropColumnIfExists(migrationBuilder, "Payments", "ProviderReceiptUrl");
        }

        private static void DropColumnIfExists(MigrationBuilder migrationBuilder, string tableName, string columnName)
        {
            migrationBuilder.Sql($@"
IF COL_LENGTH('{tableName}', '{columnName}') IS NOT NULL
BEGIN
    ALTER TABLE [{tableName}] DROP COLUMN [{columnName}];
END
");
        }
    }
}
