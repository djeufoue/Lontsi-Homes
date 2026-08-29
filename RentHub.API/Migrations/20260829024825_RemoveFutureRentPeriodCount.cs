using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentHub.API.Migrations
{
    /// <inheritdoc />
    public partial class RemoveFutureRentPeriodCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                IF EXISTS
                (
                    SELECT 1 FROM sys.check_constraints
                    WHERE [name] = N'CK_Tenancies_FutureRentPeriodCount'
                      AND [parent_object_id] = OBJECT_ID(N'[dbo].[Tenancies]')
                )
                    ALTER TABLE [Tenancies] DROP CONSTRAINT [CK_Tenancies_FutureRentPeriodCount];

                IF EXISTS
                (
                    SELECT 1 FROM sys.check_constraints
                    WHERE [name] = N'CK_Tenancies_AutoExtensionMonths'
                      AND [parent_object_id] = OBJECT_ID(N'[dbo].[Tenancies]')
                )
                    ALTER TABLE [Tenancies] DROP CONSTRAINT [CK_Tenancies_AutoExtensionMonths];

                DECLARE @futureDefault sysname;
                DECLARE @futureSql nvarchar(max);
                SELECT @futureDefault = [dc].[name]
                FROM sys.default_constraints [dc]
                INNER JOIN sys.columns [column]
                    ON [column].[object_id] = [dc].[parent_object_id]
                   AND [column].[column_id] = [dc].[parent_column_id]
                WHERE [dc].[parent_object_id] = OBJECT_ID(N'[dbo].[Tenancies]')
                  AND [column].[name] = N'FutureRentPeriodCount';
                IF @futureDefault IS NOT NULL
                BEGIN
                    SET @futureSql = N'ALTER TABLE [Tenancies] DROP CONSTRAINT ' + QUOTENAME(@futureDefault);
                    EXEC sys.sp_executesql @futureSql;
                END;

                IF COL_LENGTH('dbo.Tenancies', 'FutureRentPeriodCount') IS NOT NULL
                    ALTER TABLE [Tenancies] DROP COLUMN [FutureRentPeriodCount];

                DECLARE @legacyDefault sysname;
                DECLARE @legacySql nvarchar(max);
                SELECT @legacyDefault = [dc].[name]
                FROM sys.default_constraints [dc]
                INNER JOIN sys.columns [column]
                    ON [column].[object_id] = [dc].[parent_object_id]
                   AND [column].[column_id] = [dc].[parent_column_id]
                WHERE [dc].[parent_object_id] = OBJECT_ID(N'[dbo].[Tenancies]')
                  AND [column].[name] = N'AutoExtensionMonths';
                IF @legacyDefault IS NOT NULL
                BEGIN
                    SET @legacySql = N'ALTER TABLE [Tenancies] DROP CONSTRAINT ' + QUOTENAME(@legacyDefault);
                    EXEC sys.sp_executesql @legacySql;
                END;

                IF COL_LENGTH('dbo.Tenancies', 'AutoExtensionMonths') IS NOT NULL
                    ALTER TABLE [Tenancies] DROP COLUMN [AutoExtensionMonths];
                ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                IF COL_LENGTH('dbo.Tenancies', 'FutureRentPeriodCount') IS NULL
                    ALTER TABLE [Tenancies] ADD [FutureRentPeriodCount] int NOT NULL
                        CONSTRAINT [DF_Tenancies_FutureRentPeriodCount] DEFAULT(1) WITH VALUES;

                IF NOT EXISTS
                (
                    SELECT 1 FROM sys.check_constraints
                    WHERE [name] = N'CK_Tenancies_FutureRentPeriodCount'
                      AND [parent_object_id] = OBJECT_ID(N'[dbo].[Tenancies]')
                )
                    ALTER TABLE [Tenancies] WITH CHECK ADD CONSTRAINT [CK_Tenancies_FutureRentPeriodCount]
                    CHECK ([FutureRentPeriodCount] >= 1 AND [FutureRentPeriodCount] <= 12);
                ");
        }
    }
}
