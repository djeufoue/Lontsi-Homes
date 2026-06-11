using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentHub.API.Migrations
{
    [Migration("20260610120000_AddRentPeriodsAndTenancyLifecycle")]
    public partial class AddRentPeriodsAndTenancyLifecycle : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('Tenancies', 'RentDueDay') IS NULL
BEGIN
    ALTER TABLE [Tenancies]
        ADD [RentDueDay] int NOT NULL
            CONSTRAINT [DF_Tenancies_RentDueDay] DEFAULT(1);
END

IF COL_LENGTH('Tenancies', 'EndBehavior') IS NULL
BEGIN
    ALTER TABLE [Tenancies]
        ADD [EndBehavior] int NOT NULL
            CONSTRAINT [DF_Tenancies_EndBehavior] DEFAULT(3);
END

IF COL_LENGTH('Tenancies', 'TerminatedAt') IS NULL
BEGIN
    ALTER TABLE [Tenancies] ADD [TerminatedAt] datetimeoffset NULL;
END

IF COL_LENGTH('Tenancies', 'TerminationReason') IS NULL
BEGIN
    ALTER TABLE [Tenancies] ADD [TerminationReason] int NULL;
END

IF COL_LENGTH('Tenancies', 'TerminationNotes') IS NULL
BEGIN
    ALTER TABLE [Tenancies] ADD [TerminationNotes] nvarchar(512) NULL;
END

IF COL_LENGTH('Tenancies', 'TerminatedBy') IS NULL
BEGIN
    ALTER TABLE [Tenancies] ADD [TerminatedBy] nvarchar(max) NULL;
END
");

            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[RentPeriods]', N'U') IS NULL
BEGIN
    CREATE TABLE [RentPeriods](
        [Id] int NOT NULL IDENTITY,
        [TenancyId] int NOT NULL,
        [PeriodStart] datetimeoffset NOT NULL,
        [PeriodEnd] datetimeoffset NOT NULL,
        [DueDate] datetimeoffset NOT NULL,
        [Amount] decimal(14,2) NOT NULL,
        [PaidAmount] decimal(14,2) NOT NULL CONSTRAINT [DF_RentPeriods_PaidAmount] DEFAULT(0),
        [PaidDate] datetimeoffset NULL,
        [PaymentId] int NULL,
        [PaymentReference] nvarchar(128) NOT NULL CONSTRAINT [DF_RentPeriods_PaymentReference] DEFAULT(N''),
        [Status] int NOT NULL CONSTRAINT [DF_RentPeriods_Status] DEFAULT(0),
        [IsDeleted] bit NOT NULL CONSTRAINT [DF_RentPeriods_IsDeleted] DEFAULT(0),
        [CreatedBy] nvarchar(max) NULL,
        [CreatedAt] datetimeoffset NOT NULL CONSTRAINT [DF_RentPeriods_CreatedAt] DEFAULT(SYSDATETIMEOFFSET()),
        [UpdatedBy] nvarchar(max) NULL,
        [UpdatedAt] datetimeoffset NULL,
        [DeletedBy] nvarchar(max) NULL,
        [DeletedAt] datetimeoffset NULL,
        CONSTRAINT [PK_RentPeriods] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_RentPeriods_Tenancies_TenancyId] FOREIGN KEY ([TenancyId]) REFERENCES [Tenancies]([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_RentPeriods_Payments_PaymentId] FOREIGN KEY ([PaymentId]) REFERENCES [Payments]([Id])
    );
END
");

            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_RentPeriods_TenancyId_PeriodStart' AND [object_id] = OBJECT_ID(N'[RentPeriods]'))
BEGIN
    CREATE UNIQUE INDEX [IX_RentPeriods_TenancyId_PeriodStart]
    ON [RentPeriods]([TenancyId], [PeriodStart])
    WHERE [IsDeleted] = 0;
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_RentPeriods_TenancyId_Status_DueDate' AND [object_id] = OBJECT_ID(N'[RentPeriods]'))
BEGIN
    CREATE INDEX [IX_RentPeriods_TenancyId_Status_DueDate]
    ON [RentPeriods]([TenancyId], [Status], [DueDate]);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_RentPeriods_PaymentId' AND [object_id] = OBJECT_ID(N'[RentPeriods]'))
BEGIN
    CREATE INDEX [IX_RentPeriods_PaymentId]
    ON [RentPeriods]([PaymentId]);
END
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[RentPeriods]', N'U') IS NOT NULL
BEGIN
    DROP TABLE [RentPeriods];
END
");

            DropColumnIfExists(migrationBuilder, "Tenancies", "TerminatedBy");
            DropColumnIfExists(migrationBuilder, "Tenancies", "TerminationNotes");
            DropColumnIfExists(migrationBuilder, "Tenancies", "TerminationReason");
            DropColumnIfExists(migrationBuilder, "Tenancies", "TerminatedAt");
            DropColumnIfExists(migrationBuilder, "Tenancies", "EndBehavior");
            DropColumnIfExists(migrationBuilder, "Tenancies", "RentDueDay");
        }

        private static void DropColumnIfExists(MigrationBuilder migrationBuilder, string tableName, string columnName)
        {
            migrationBuilder.Sql($@"
DECLARE @constraintName nvarchar(200);
SELECT @constraintName = dc.name
FROM sys.default_constraints dc
JOIN sys.columns c ON c.default_object_id = dc.object_id
JOIN sys.tables t ON t.object_id = c.object_id
WHERE t.name = N'{tableName}' AND c.name = N'{columnName}';

IF @constraintName IS NOT NULL
BEGIN
    EXEC(N'ALTER TABLE [{tableName}] DROP CONSTRAINT [' + @constraintName + N']');
END

IF COL_LENGTH('{tableName}', '{columnName}') IS NOT NULL
BEGIN
    ALTER TABLE [{tableName}] DROP COLUMN [{columnName}];
END
");
        }
    }
}
