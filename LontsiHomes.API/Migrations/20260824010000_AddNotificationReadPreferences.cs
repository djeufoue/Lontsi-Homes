using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using LontsiHomes.API.Data;

#nullable disable

namespace LontsiHomes.API.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260824010000_AddNotificationReadPreferences")]
    public partial class AddNotificationReadPreferences : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF COL_LENGTH('AspNetUsers', 'ConversationEmailNotificationsEnabled') IS NULL
                BEGIN
                    ALTER TABLE [AspNetUsers]
                    ADD [ConversationEmailNotificationsEnabled] bit NOT NULL
                        CONSTRAINT [DF_AspNetUsers_ConversationEmailNotificationsEnabled] DEFAULT(1) WITH VALUES;
                END

                IF OBJECT_ID(N'[TenancyExtensionRequests]', N'U') IS NOT NULL
                   AND COL_LENGTH('TenancyExtensionRequests', 'DecisionViewedAt') IS NULL
                BEGIN
                    ALTER TABLE [TenancyExtensionRequests]
                    ADD [DecisionViewedAt] datetimeoffset NULL;
                END

                IF OBJECT_ID(N'[TenancyTerminationRequests]', N'U') IS NOT NULL
                   AND COL_LENGTH('TenancyTerminationRequests', 'DecisionViewedAt') IS NULL
                BEGIN
                    ALTER TABLE [TenancyTerminationRequests]
                    ADD [DecisionViewedAt] datetimeoffset NULL;
                END
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF COL_LENGTH('AspNetUsers', 'ConversationEmailNotificationsEnabled') IS NOT NULL
                    ALTER TABLE [AspNetUsers] DROP COLUMN [ConversationEmailNotificationsEnabled];

                IF OBJECT_ID(N'[TenancyExtensionRequests]', N'U') IS NOT NULL
                   AND COL_LENGTH('TenancyExtensionRequests', 'DecisionViewedAt') IS NOT NULL
                    ALTER TABLE [TenancyExtensionRequests] DROP COLUMN [DecisionViewedAt];

                IF OBJECT_ID(N'[TenancyTerminationRequests]', N'U') IS NOT NULL
                   AND COL_LENGTH('TenancyTerminationRequests', 'DecisionViewedAt') IS NOT NULL
                    ALTER TABLE [TenancyTerminationRequests] DROP COLUMN [DecisionViewedAt];
                """);
        }
    }
}
