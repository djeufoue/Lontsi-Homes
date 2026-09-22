using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using LontsiHomes.API.Data;

#nullable disable

namespace LontsiHomes.API.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260816190000_AddTenancyRenewalWorkflow")]
    public partial class AddTenancyRenewalWorkflow : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OriginalEndDate",
                table: "TenancyExtensionRequests",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                table: "TenancyExtensionRequests",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RenewalReminderSentAt",
                table: "Tenancies",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RenewalReminderSentForEndDate",
                table: "Tenancies",
                type: "datetimeoffset",
                nullable: true);

            // Keep the newest pending request and preserve older duplicates as history
            // before adding the database-level concurrency guard.
            migrationBuilder.Sql(
                """
                ;WITH RankedPendingRequests AS
                (
                    SELECT [Id],
                           ROW_NUMBER() OVER
                           (
                               PARTITION BY [TenancyId]
                               ORDER BY [CreatedAt] DESC, [Id] DESC
                           ) AS [PendingRank]
                    FROM [TenancyExtensionRequests]
                    WHERE [Status] = 0 AND [IsDeleted] = 0
                )
                UPDATE [request]
                SET [Status] = 2,
                    [RejectionReason] = COALESCE(
                        [request].[RejectionReason],
                        N'Superseded by a newer pending request during the tenancy renewal migration.'),
                    [UpdatedAt] = SYSUTCDATETIME(),
                    [UpdatedBy] = COALESCE([request].[UpdatedBy], N'system:migration')
                FROM [TenancyExtensionRequests] AS [request]
                INNER JOIN [RankedPendingRequests] AS [ranked]
                    ON [request].[Id] = [ranked].[Id]
                WHERE [ranked].[PendingRank] > 1;
                """);

            migrationBuilder.DropIndex(
                name: "IX_TenancyExtensionRequests_TenancyId",
                table: "TenancyExtensionRequests");

            migrationBuilder.CreateIndex(
                name: "IX_TenancyExtensionRequests_TenancyId",
                table: "TenancyExtensionRequests",
                column: "TenancyId",
                unique: true,
                filter: "[Status] = 0 AND [IsDeleted] = 0");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TenancyExtensionRequests_TenancyId",
                table: "TenancyExtensionRequests");

            migrationBuilder.CreateIndex(
                name: "IX_TenancyExtensionRequests_TenancyId",
                table: "TenancyExtensionRequests",
                column: "TenancyId");

            migrationBuilder.DropColumn(
                name: "OriginalEndDate",
                table: "TenancyExtensionRequests");

            migrationBuilder.DropColumn(
                name: "RejectionReason",
                table: "TenancyExtensionRequests");

            migrationBuilder.DropColumn(
                name: "RenewalReminderSentAt",
                table: "Tenancies");

            migrationBuilder.DropColumn(
                name: "RenewalReminderSentForEndDate",
                table: "Tenancies");
        }
    }
}
