using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using LontsiHomes.API.Data;

#nullable disable

namespace LontsiHomes.API.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260823235900_AddInternalPropertyTeamConversations")]
    public partial class AddInternalPropertyTeamConversations : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE [name] = N'IX_ApartmentConversations_ApartmentId_VisitorId'
                      AND [object_id] = OBJECT_ID(N'[dbo].[ApartmentConversations]'))
                    DROP INDEX [IX_ApartmentConversations_ApartmentId_VisitorId]
                    ON [ApartmentConversations];
                """);

            migrationBuilder.Sql(
                """
                IF COL_LENGTH('dbo.ApartmentConversations', 'IsPropertyTeamConversation') IS NULL
                BEGIN
                    ALTER TABLE [ApartmentConversations]
                    ADD [IsPropertyTeamConversation] bit NOT NULL
                        CONSTRAINT [DF_ApartmentConversations_IsPropertyTeamConversation] DEFAULT(0) WITH VALUES;
                END;
                """);

            migrationBuilder.Sql(
                """
                IF NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE [name] = N'IX_ApartmentConversations_ApartmentId_VisitorId_IsPropertyTeamConversation'
                      AND [object_id] = OBJECT_ID(N'[dbo].[ApartmentConversations]'))
                    CREATE UNIQUE INDEX [IX_ApartmentConversations_ApartmentId_VisitorId_IsPropertyTeamConversation]
                    ON [ApartmentConversations]([ApartmentId], [VisitorId], [IsPropertyTeamConversation]);
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE [name] = N'IX_ApartmentConversations_ApartmentId_VisitorId_IsPropertyTeamConversation'
                      AND [object_id] = OBJECT_ID(N'[dbo].[ApartmentConversations]'))
                    DROP INDEX [IX_ApartmentConversations_ApartmentId_VisitorId_IsPropertyTeamConversation]
                    ON [ApartmentConversations];
                """);

            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1 FROM sys.default_constraints
                    WHERE [name] = N'DF_ApartmentConversations_IsPropertyTeamConversation'
                      AND [parent_object_id] = OBJECT_ID(N'[dbo].[ApartmentConversations]'))
                    ALTER TABLE [ApartmentConversations]
                    DROP CONSTRAINT [DF_ApartmentConversations_IsPropertyTeamConversation];
                """);

            migrationBuilder.Sql(
                """
                IF COL_LENGTH('dbo.ApartmentConversations', 'IsPropertyTeamConversation') IS NOT NULL
                    ALTER TABLE [ApartmentConversations] DROP COLUMN [IsPropertyTeamConversation];
                """);

            migrationBuilder.Sql(
                """
                IF NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE [name] = N'IX_ApartmentConversations_ApartmentId_VisitorId'
                      AND [object_id] = OBJECT_ID(N'[dbo].[ApartmentConversations]'))
                    CREATE UNIQUE INDEX [IX_ApartmentConversations_ApartmentId_VisitorId]
                    ON [ApartmentConversations]([ApartmentId], [VisitorId]);
                """);
        }
    }
}
