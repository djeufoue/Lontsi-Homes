using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RentHub.API.Data;

#nullable disable

namespace RentHub.API.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260524193000_AddPaymentIdempotencyConstraints")]
    public partial class AddPaymentIdempotencyConstraints : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('Payments', 'RequestKey') IS NULL
BEGIN
    ALTER TABLE [Payments]
    ADD [RequestKey] nvarchar(160) NOT NULL
        CONSTRAINT [DF_Payments_RequestKey] DEFAULT(N'');
END
");

            DropDefaultConstraint(migrationBuilder, "Payments", "TransactionId");
            migrationBuilder.Sql(@"
IF COL_LENGTH('Payments', 'TransactionId') IS NULL
BEGIN
    ALTER TABLE [Payments]
    ADD [TransactionId] nvarchar(128) NOT NULL
        CONSTRAINT [DF_Payments_TransactionId] DEFAULT(CONVERT(nvarchar(36), NEWID()));
END
ELSE
BEGIN
    UPDATE [Payments]
    SET [TransactionId] = CONVERT(nvarchar(36), NEWID())
    WHERE [TransactionId] IS NULL OR [TransactionId] = N'';

    UPDATE [Payments]
    SET [TransactionId] = LEFT([TransactionId], 128)
    WHERE LEN([TransactionId]) > 128;

    ALTER TABLE [Payments] ALTER COLUMN [TransactionId] nvarchar(128) NOT NULL;
END
");

            migrationBuilder.Sql(@"
IF COL_LENGTH('UserSubscriptions', 'PaymentMethod') IS NULL
BEGIN
    ALTER TABLE [UserSubscriptions]
    ADD [PaymentMethod] int NULL;
END

IF COL_LENGTH('UserSubscriptions', 'PaymentStatus') IS NULL
BEGIN
    ALTER TABLE [UserSubscriptions]
    ADD [PaymentStatus] int NOT NULL
        CONSTRAINT [DF_UserSubscriptions_PaymentStatus] DEFAULT(0);
END

IF COL_LENGTH('UserSubscriptions', 'AllowAutomaticCardPayments') IS NULL
BEGIN
    ALTER TABLE [UserSubscriptions]
    ADD [AllowAutomaticCardPayments] bit NOT NULL
        CONSTRAINT [DF_UserSubscriptions_AllowAutomaticCardPayments] DEFAULT(0);
END

IF COL_LENGTH('UserSubscriptions', 'PaymentCompletedAt') IS NULL
BEGIN
    ALTER TABLE [UserSubscriptions]
    ADD [PaymentCompletedAt] datetimeoffset NULL;
END

IF COL_LENGTH('UserSubscriptions', 'PaymentAttemptCount') IS NULL
BEGIN
    ALTER TABLE [UserSubscriptions]
    ADD [PaymentAttemptCount] int NOT NULL
        CONSTRAINT [DF_UserSubscriptions_PaymentAttemptCount] DEFAULT(0);
END
");

            DropDefaultConstraint(migrationBuilder, "UserSubscriptions", "PaymentReference");
            migrationBuilder.Sql(@"
IF COL_LENGTH('UserSubscriptions', 'PaymentReference') IS NULL
BEGIN
    ALTER TABLE [UserSubscriptions]
    ADD [PaymentReference] nvarchar(128) NOT NULL
        CONSTRAINT [DF_UserSubscriptions_PaymentReference] DEFAULT(N'');
END
ELSE
BEGIN
    UPDATE [UserSubscriptions]
    SET [PaymentReference] = ISNULL(LEFT([PaymentReference], 128), N'')
    WHERE [PaymentReference] IS NULL OR LEN([PaymentReference]) > 128;

    ALTER TABLE [UserSubscriptions] ALTER COLUMN [PaymentReference] nvarchar(128) NOT NULL;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.default_constraints dc
        INNER JOIN sys.columns c ON c.default_object_id = dc.object_id
        WHERE dc.parent_object_id = OBJECT_ID(N'[UserSubscriptions]')
          AND c.name = N'PaymentReference'
    )
    BEGIN
        ALTER TABLE [UserSubscriptions]
        ADD CONSTRAINT [DF_UserSubscriptions_PaymentReference] DEFAULT(N'') FOR [PaymentReference];
    END
END
");

            migrationBuilder.Sql(@"
IF COL_LENGTH('UserSubscriptions', 'PaymentProviderTransactionId') IS NULL
BEGIN
    ALTER TABLE [UserSubscriptions]
    ADD [PaymentProviderTransactionId] nvarchar(128) NULL;
END
ELSE
BEGIN
    UPDATE [UserSubscriptions]
    SET [PaymentProviderTransactionId] = LEFT([PaymentProviderTransactionId], 128)
    WHERE [PaymentProviderTransactionId] IS NOT NULL
      AND LEN([PaymentProviderTransactionId]) > 128;

    ALTER TABLE [UserSubscriptions] ALTER COLUMN [PaymentProviderTransactionId] nvarchar(128) NULL;
END
");

            migrationBuilder.Sql(@"
IF COL_LENGTH('UserSubscriptions', 'PaymentAuthorizationUrl') IS NULL
BEGIN
    ALTER TABLE [UserSubscriptions]
    ADD [PaymentAuthorizationUrl] nvarchar(2048) NULL;
END
ELSE
BEGIN
    UPDATE [UserSubscriptions]
    SET [PaymentAuthorizationUrl] = LEFT([PaymentAuthorizationUrl], 2048)
    WHERE [PaymentAuthorizationUrl] IS NOT NULL
      AND LEN([PaymentAuthorizationUrl]) > 2048;

    ALTER TABLE [UserSubscriptions] ALTER COLUMN [PaymentAuthorizationUrl] nvarchar(2048) NULL;
END
");

            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[PaymentWebhookEvents]', N'U') IS NULL
BEGIN
    CREATE TABLE [PaymentWebhookEvents]
    (
        [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_PaymentWebhookEvents] PRIMARY KEY,
        [Provider] nvarchar(50) NOT NULL,
        [EventKey] nvarchar(160) NOT NULL,
        [EventType] nvarchar(100) NULL,
        [PaymentReference] nvarchar(128) NULL,
        [ProviderTransactionId] nvarchar(128) NULL,
        [PayloadHash] nvarchar(64) NULL,
        [ReceivedAt] datetimeoffset NOT NULL
            CONSTRAINT [DF_PaymentWebhookEvents_ReceivedAt] DEFAULT(SYSDATETIMEOFFSET()),
        [ProcessedAt] datetimeoffset NULL,
        [ProcessingStatus] nvarchar(30) NOT NULL
            CONSTRAINT [DF_PaymentWebhookEvents_ProcessingStatus] DEFAULT(N'Received'),
        [ProcessingMessage] nvarchar(max) NULL
    );
END
");

            migrationBuilder.Sql(@"
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Payments_RequestKey'
      AND [object_id] = OBJECT_ID(N'[Payments]')
)
BEGIN
    CREATE UNIQUE INDEX [IX_Payments_RequestKey]
    ON [Payments]([RequestKey])
    WHERE [RequestKey] IS NOT NULL AND [RequestKey] <> N'' AND [IsDeleted] = 0;
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Payments_TransactionId'
      AND [object_id] = OBJECT_ID(N'[Payments]')
)
BEGIN
    CREATE UNIQUE INDEX [IX_Payments_TransactionId]
    ON [Payments]([TransactionId])
    WHERE [TransactionId] IS NOT NULL AND [TransactionId] <> N'' AND [IsDeleted] = 0;
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Payments_TenancyId_Status_PaymentDate'
      AND [object_id] = OBJECT_ID(N'[Payments]')
)
BEGIN
    CREATE INDEX [IX_Payments_TenancyId_Status_PaymentDate]
    ON [Payments]([TenancyId], [Status], [PaymentDate]);
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_UserSubscriptions_PaymentReference'
      AND [object_id] = OBJECT_ID(N'[UserSubscriptions]')
)
BEGIN
    CREATE UNIQUE INDEX [IX_UserSubscriptions_PaymentReference]
    ON [UserSubscriptions]([PaymentReference])
    WHERE [PaymentReference] IS NOT NULL AND [PaymentReference] <> N'' AND [IsDeleted] = 0;
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_UserSubscriptions_PaymentProviderTransactionId'
      AND [object_id] = OBJECT_ID(N'[UserSubscriptions]')
)
BEGIN
    CREATE UNIQUE INDEX [IX_UserSubscriptions_PaymentProviderTransactionId]
    ON [UserSubscriptions]([PaymentProviderTransactionId])
    WHERE [PaymentProviderTransactionId] IS NOT NULL AND [PaymentProviderTransactionId] <> N'' AND [IsDeleted] = 0;
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_UserSubscriptions_UserId_SubscriptionPlanId_OpenPayment'
      AND [object_id] = OBJECT_ID(N'[UserSubscriptions]')
)
BEGIN
    CREATE UNIQUE INDEX [IX_UserSubscriptions_UserId_SubscriptionPlanId_OpenPayment]
    ON [UserSubscriptions]([UserId], [SubscriptionPlanId])
    WHERE [IsDeleted] = 0 AND [IsApproved] = 0 AND [PaymentStatus] <> 1;
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_PaymentWebhookEvents_Provider_EventKey'
      AND [object_id] = OBJECT_ID(N'[PaymentWebhookEvents]')
)
BEGIN
    CREATE UNIQUE INDEX [IX_PaymentWebhookEvents_Provider_EventKey]
    ON [PaymentWebhookEvents]([Provider], [EventKey]);
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_PaymentWebhookEvents_PaymentReference'
      AND [object_id] = OBJECT_ID(N'[PaymentWebhookEvents]')
)
BEGIN
    CREATE INDEX [IX_PaymentWebhookEvents_PaymentReference]
    ON [PaymentWebhookEvents]([PaymentReference]);
END
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_UserSubscriptions_UserId_SubscriptionPlanId_OpenPayment'
      AND [object_id] = OBJECT_ID(N'[UserSubscriptions]')
)
BEGIN
    DROP INDEX [IX_UserSubscriptions_UserId_SubscriptionPlanId_OpenPayment] ON [UserSubscriptions];
END

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_UserSubscriptions_PaymentProviderTransactionId'
      AND [object_id] = OBJECT_ID(N'[UserSubscriptions]')
)
BEGIN
    DROP INDEX [IX_UserSubscriptions_PaymentProviderTransactionId] ON [UserSubscriptions];
END

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_UserSubscriptions_PaymentReference'
      AND [object_id] = OBJECT_ID(N'[UserSubscriptions]')
)
BEGIN
    DROP INDEX [IX_UserSubscriptions_PaymentReference] ON [UserSubscriptions];
END

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Payments_TenancyId_Status_PaymentDate'
      AND [object_id] = OBJECT_ID(N'[Payments]')
)
BEGIN
    DROP INDEX [IX_Payments_TenancyId_Status_PaymentDate] ON [Payments];
END

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Payments_TransactionId'
      AND [object_id] = OBJECT_ID(N'[Payments]')
)
BEGIN
    DROP INDEX [IX_Payments_TransactionId] ON [Payments];
END

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Payments_RequestKey'
      AND [object_id] = OBJECT_ID(N'[Payments]')
)
BEGIN
    DROP INDEX [IX_Payments_RequestKey] ON [Payments];
END

IF OBJECT_ID(N'[PaymentWebhookEvents]', N'U') IS NOT NULL
BEGIN
    DROP TABLE [PaymentWebhookEvents];
END
");

            DropDefaultConstraint(migrationBuilder, "Payments", "RequestKey");
            migrationBuilder.Sql(@"
IF COL_LENGTH('Payments', 'RequestKey') IS NOT NULL
BEGIN
    ALTER TABLE [Payments] DROP COLUMN [RequestKey];
END
");

            DropDefaultConstraint(migrationBuilder, "UserSubscriptions", "PaymentAttemptCount");
            migrationBuilder.Sql(@"
IF COL_LENGTH('UserSubscriptions', 'PaymentAttemptCount') IS NOT NULL
BEGIN
    ALTER TABLE [UserSubscriptions] DROP COLUMN [PaymentAttemptCount];
END
");
        }

        private static void DropDefaultConstraint(
            MigrationBuilder migrationBuilder,
            string tableName,
            string columnName)
        {
            var escapedTableName = tableName.Replace("]", "]]");
            var escapedColumnName = columnName.Replace("'", "''");

            migrationBuilder.Sql($@"
DECLARE @constraintName sysname;
DECLARE @sql nvarchar(max);

SELECT @constraintName = dc.name
FROM sys.default_constraints dc
INNER JOIN sys.columns c ON c.default_object_id = dc.object_id
WHERE dc.parent_object_id = OBJECT_ID(N'[{escapedTableName}]')
  AND c.name = N'{escapedColumnName}';

IF @constraintName IS NOT NULL
BEGIN
    SET @sql = N'ALTER TABLE [{escapedTableName}] DROP CONSTRAINT ' + QUOTENAME(@constraintName);
    EXEC sp_executesql @sql;
END
");
        }
    }
}
