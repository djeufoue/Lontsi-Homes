using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using LontsiHomes.API.Data;

#nullable disable

namespace LontsiHomes.API.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260823220000_AddPropertyConversationMessaging")]
    public partial class AddPropertyConversationMessaging : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF COL_LENGTH('dbo.ConversationMessages', 'IsPropertyBroadcast') IS NULL
                BEGIN
                    ALTER TABLE [ConversationMessages]
                    ADD [IsPropertyBroadcast] bit NOT NULL
                        CONSTRAINT [DF_ConversationMessages_IsPropertyBroadcast] DEFAULT(0) WITH VALUES;
                END;
                """);

            migrationBuilder.Sql(
                """
                IF OBJECT_ID(N'[dbo].[ConversationReadStates]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [ConversationReadStates]
                    (
                        [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_ConversationReadStates] PRIMARY KEY,
                        [ConversationId] int NOT NULL,
                        [UserId] nvarchar(450) NOT NULL,
                        [LastReadAt] datetimeoffset NOT NULL,
                        CONSTRAINT [FK_ConversationReadStates_ApartmentConversations_ConversationId]
                            FOREIGN KEY ([ConversationId]) REFERENCES [ApartmentConversations]([Id]) ON DELETE CASCADE,
                        CONSTRAINT [FK_ConversationReadStates_AspNetUsers_UserId]
                            FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers]([Id])
                    );
                END;
                """);

            migrationBuilder.Sql(
                """
                IF NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE [name] = N'IX_ConversationReadStates_ConversationId_UserId'
                      AND [object_id] = OBJECT_ID(N'[dbo].[ConversationReadStates]'))
                    CREATE UNIQUE INDEX [IX_ConversationReadStates_ConversationId_UserId]
                    ON [ConversationReadStates]([ConversationId], [UserId]);
                """);

            migrationBuilder.Sql(
                """
                IF NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE [name] = N'IX_ConversationReadStates_UserId'
                      AND [object_id] = OBJECT_ID(N'[dbo].[ConversationReadStates]'))
                    CREATE INDEX [IX_ConversationReadStates_UserId]
                    ON [ConversationReadStates]([UserId]);
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF OBJECT_ID(N'[dbo].[ConversationReadStates]', N'U') IS NOT NULL
                    DROP TABLE [ConversationReadStates];
                """);

            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1 FROM sys.default_constraints
                    WHERE [name] = N'DF_ConversationMessages_IsPropertyBroadcast'
                      AND [parent_object_id] = OBJECT_ID(N'[dbo].[ConversationMessages]'))
                    ALTER TABLE [ConversationMessages]
                    DROP CONSTRAINT [DF_ConversationMessages_IsPropertyBroadcast];
                """);

            migrationBuilder.Sql(
                """
                IF COL_LENGTH('dbo.ConversationMessages', 'IsPropertyBroadcast') IS NOT NULL
                    ALTER TABLE [ConversationMessages] DROP COLUMN [IsPropertyBroadcast];
                """);
        }
    }
}
