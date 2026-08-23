using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RentHub.API.Data;

#nullable disable

namespace RentHub.API.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260823190000_AddTenancyAutoExtensionMonths")]
    public partial class AddTenancyAutoExtensionMonths : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF COL_LENGTH('dbo.Tenancies', 'AutoExtensionMonths') IS NULL
                BEGIN
                    ALTER TABLE [Tenancies]
                    ADD [AutoExtensionMonths] int NOT NULL
                        CONSTRAINT [DF_Tenancies_AutoExtensionMonths] DEFAULT(1) WITH VALUES;
                END;
                """);

            migrationBuilder.Sql(
                """
                UPDATE [Tenancies]
                SET [EndBehavior] = 3, [EndDate] = NULL
                WHERE [EndBehavior] = 2;
                """);

            migrationBuilder.Sql(
                """
                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.check_constraints
                    WHERE [name] = N'CK_Tenancies_AutoExtensionMonths'
                      AND [parent_object_id] = OBJECT_ID(N'[dbo].[Tenancies]'))
                BEGIN
                    ALTER TABLE [Tenancies] WITH CHECK
                    ADD CONSTRAINT [CK_Tenancies_AutoExtensionMonths]
                    CHECK ([AutoExtensionMonths] >= 1 AND [AutoExtensionMonths] <= 12);
                END;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1
                    FROM sys.check_constraints
                    WHERE [name] = N'CK_Tenancies_AutoExtensionMonths'
                      AND [parent_object_id] = OBJECT_ID(N'[dbo].[Tenancies]'))
                    ALTER TABLE [Tenancies] DROP CONSTRAINT [CK_Tenancies_AutoExtensionMonths];
                """);

            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1
                    FROM sys.default_constraints
                    WHERE [name] = N'DF_Tenancies_AutoExtensionMonths'
                      AND [parent_object_id] = OBJECT_ID(N'[dbo].[Tenancies]'))
                    ALTER TABLE [Tenancies] DROP CONSTRAINT [DF_Tenancies_AutoExtensionMonths];
                """);

            migrationBuilder.Sql(
                """
                IF COL_LENGTH('dbo.Tenancies', 'AutoExtensionMonths') IS NOT NULL
                    ALTER TABLE [Tenancies] DROP COLUMN [AutoExtensionMonths];
                """);
        }
    }
}
