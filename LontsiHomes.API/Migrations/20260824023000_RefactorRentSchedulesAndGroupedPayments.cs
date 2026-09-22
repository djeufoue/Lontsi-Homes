using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using LontsiHomes.API.Data;

#nullable disable

namespace LontsiHomes.API.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260824023000_RefactorRentSchedulesAndGroupedPayments")]
    public partial class RefactorRentSchedulesAndGroupedPayments : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_Tenancies_AutoExtensionMonths' AND [parent_object_id] = OBJECT_ID(N'[dbo].[Tenancies]'))
                    ALTER TABLE [Tenancies] DROP CONSTRAINT [CK_Tenancies_AutoExtensionMonths];

                IF COL_LENGTH('dbo.Tenancies', 'FutureRentPeriodCount') IS NULL
                   AND COL_LENGTH('dbo.Tenancies', 'AutoExtensionMonths') IS NOT NULL
                    EXEC sp_rename N'dbo.Tenancies.AutoExtensionMonths', N'FutureRentPeriodCount', 'COLUMN';

                IF COL_LENGTH('dbo.Tenancies', 'FutureRentPeriodCount') IS NULL
                    ALTER TABLE [Tenancies] ADD [FutureRentPeriodCount] int NOT NULL CONSTRAINT [DF_Tenancies_FutureRentPeriodCount] DEFAULT(1) WITH VALUES;

                IF COL_LENGTH('dbo.Tenancies', 'PaymentIntervalMonths') IS NULL
                    ALTER TABLE [Tenancies] ADD [PaymentIntervalMonths] int NOT NULL CONSTRAINT [DF_Tenancies_PaymentIntervalMonths] DEFAULT(1) WITH VALUES;

                IF COL_LENGTH('dbo.Tenancies', 'RentTrackingStartDate') IS NULL
                    ALTER TABLE [Tenancies] ADD [RentTrackingStartDate] datetimeoffset NULL;

                IF COL_LENGTH('dbo.Tenancies', 'RentScheduleNeedsReview') IS NULL
                    ALTER TABLE [Tenancies] ADD [RentScheduleNeedsReview] bit NOT NULL CONSTRAINT [DF_Tenancies_RentScheduleNeedsReview] DEFAULT(0) WITH VALUES;

                IF COL_LENGTH('dbo.RentPeriods', 'BillingGroupSequence') IS NULL
                BEGIN
                    ALTER TABLE [RentPeriods] ADD [BillingGroupSequence] int NOT NULL CONSTRAINT [DF_RentPeriods_BillingGroupSequence] DEFAULT(0) WITH VALUES;
                    EXEC(N';WITH [OrderedPeriods] AS
                    (
                        SELECT [Id], ROW_NUMBER() OVER (PARTITION BY [TenancyId] ORDER BY [PeriodStart], [Id]) - 1 AS [Sequence]
                        FROM [RentPeriods]
                        WHERE [IsDeleted] = 0
                    )
                    UPDATE [period]
                    SET [BillingGroupSequence] = [ordered].[Sequence]
                    FROM [RentPeriods] [period]
                    INNER JOIN [OrderedPeriods] [ordered] ON [ordered].[Id] = [period].[Id];');
                END;

                IF COL_LENGTH('dbo.RentReminders', 'InvalidatedAt') IS NULL
                    ALTER TABLE [RentReminders] ADD [InvalidatedAt] datetimeoffset NULL;

                IF COL_LENGTH('dbo.RentReminders', 'InvalidationReason') IS NULL
                    ALTER TABLE [RentReminders] ADD [InvalidationReason] nvarchar(512) NOT NULL CONSTRAINT [DF_RentReminders_InvalidationReason] DEFAULT(N'') WITH VALUES;
                """);

            // SQL Server binds column references when compiling a batch. Keep all
            // references to newly-created columns in a later migration command.
            migrationBuilder.Sql(
                """
                UPDATE [Tenancies]
                SET [RentTrackingStartDate] = [StartDate]
                WHERE [RentTrackingStartDate] IS NULL;

                IF EXISTS
                (
                    SELECT 1 FROM sys.columns
                    WHERE [object_id] = OBJECT_ID(N'[dbo].[Tenancies]')
                      AND [name] = N'RentTrackingStartDate'
                      AND [is_nullable] = 1
                )
                    ALTER TABLE [Tenancies] ALTER COLUMN [RentTrackingStartDate] datetimeoffset NOT NULL;

                UPDATE [tenancy]
                SET [RentScheduleNeedsReview] = 1
                FROM [Tenancies] [tenancy]
                WHERE [tenancy].[IsDeleted] = 0
                  AND DAY([tenancy].[StartDate]) <> 1
                  AND EXISTS
                  (
                      SELECT 1
                      FROM [RentPeriods] [period]
                      WHERE [period].[TenancyId] = [tenancy].[Id]
                        AND [period].[IsDeleted] = 0
                        AND [period].[PeriodStart] > [tenancy].[StartDate]
                        AND DAY([period].[PeriodStart]) = 1
                  );

                IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_Tenancies_FutureRentPeriodCount' AND [parent_object_id] = OBJECT_ID(N'[dbo].[Tenancies]'))
                    ALTER TABLE [Tenancies] WITH CHECK ADD CONSTRAINT [CK_Tenancies_FutureRentPeriodCount] CHECK ([FutureRentPeriodCount] >= 1 AND [FutureRentPeriodCount] <= 12);

                IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_Tenancies_PaymentIntervalMonths' AND [parent_object_id] = OBJECT_ID(N'[dbo].[Tenancies]'))
                    ALTER TABLE [Tenancies] WITH CHECK ADD CONSTRAINT [CK_Tenancies_PaymentIntervalMonths] CHECK ([PaymentIntervalMonths] >= 1 AND [PaymentIntervalMonths] <= 12);

                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_RentPeriods_TenancyId_BillingGroupSequence_PeriodStart' AND [object_id] = OBJECT_ID(N'[dbo].[RentPeriods]'))
                    CREATE INDEX [IX_RentPeriods_TenancyId_BillingGroupSequence_PeriodStart] ON [RentPeriods] ([TenancyId], [BillingGroupSequence], [PeriodStart]);
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_RentPeriods_TenancyId_BillingGroupSequence_PeriodStart' AND [object_id] = OBJECT_ID(N'[dbo].[RentPeriods]'))
                    DROP INDEX [IX_RentPeriods_TenancyId_BillingGroupSequence_PeriodStart] ON [RentPeriods];
                IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_Tenancies_PaymentIntervalMonths' AND [parent_object_id] = OBJECT_ID(N'[dbo].[Tenancies]'))
                    ALTER TABLE [Tenancies] DROP CONSTRAINT [CK_Tenancies_PaymentIntervalMonths];
                IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_Tenancies_FutureRentPeriodCount' AND [parent_object_id] = OBJECT_ID(N'[dbo].[Tenancies]'))
                    ALTER TABLE [Tenancies] DROP CONSTRAINT [CK_Tenancies_FutureRentPeriodCount];

                IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE [name] = N'DF_RentReminders_InvalidationReason' AND [parent_object_id] = OBJECT_ID(N'[dbo].[RentReminders]')) ALTER TABLE [RentReminders] DROP CONSTRAINT [DF_RentReminders_InvalidationReason];
                IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE [name] = N'DF_RentPeriods_BillingGroupSequence' AND [parent_object_id] = OBJECT_ID(N'[dbo].[RentPeriods]')) ALTER TABLE [RentPeriods] DROP CONSTRAINT [DF_RentPeriods_BillingGroupSequence];
                IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE [name] = N'DF_Tenancies_RentScheduleNeedsReview' AND [parent_object_id] = OBJECT_ID(N'[dbo].[Tenancies]')) ALTER TABLE [Tenancies] DROP CONSTRAINT [DF_Tenancies_RentScheduleNeedsReview];
                IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE [name] = N'DF_Tenancies_PaymentIntervalMonths' AND [parent_object_id] = OBJECT_ID(N'[dbo].[Tenancies]')) ALTER TABLE [Tenancies] DROP CONSTRAINT [DF_Tenancies_PaymentIntervalMonths];

                IF COL_LENGTH('dbo.RentReminders', 'InvalidationReason') IS NOT NULL ALTER TABLE [RentReminders] DROP COLUMN [InvalidationReason];
                IF COL_LENGTH('dbo.RentReminders', 'InvalidatedAt') IS NOT NULL ALTER TABLE [RentReminders] DROP COLUMN [InvalidatedAt];
                IF COL_LENGTH('dbo.RentPeriods', 'BillingGroupSequence') IS NOT NULL ALTER TABLE [RentPeriods] DROP COLUMN [BillingGroupSequence];
                IF COL_LENGTH('dbo.Tenancies', 'RentScheduleNeedsReview') IS NOT NULL ALTER TABLE [Tenancies] DROP COLUMN [RentScheduleNeedsReview];
                IF COL_LENGTH('dbo.Tenancies', 'RentTrackingStartDate') IS NOT NULL ALTER TABLE [Tenancies] DROP COLUMN [RentTrackingStartDate];
                IF COL_LENGTH('dbo.Tenancies', 'PaymentIntervalMonths') IS NOT NULL ALTER TABLE [Tenancies] DROP COLUMN [PaymentIntervalMonths];

                IF COL_LENGTH('dbo.Tenancies', 'AutoExtensionMonths') IS NULL
                   AND COL_LENGTH('dbo.Tenancies', 'FutureRentPeriodCount') IS NOT NULL
                    EXEC sp_rename N'dbo.Tenancies.FutureRentPeriodCount', N'AutoExtensionMonths', 'COLUMN';
                """);
        }
    }
}
