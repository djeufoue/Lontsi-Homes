using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LontsiHomes.API.Migrations
{
    /// <inheritdoc />
    public partial class AddInfobipMessagingAndWhatsAppConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Some production databases already removed this obsolete setting manually.
            // Preserve all user numbers: fail clearly instead of truncating legacy values.
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM [AspNetUsers] WHERE DATALENGTH([WhatsAppPhoneNumber]) > 32)
                    THROW 51001, 'WhatsApp migration blocked: legacy phone numbers exceed 16 characters. Normalize these values before retrying; no data was truncated.', 1;
                IF COL_LENGTH(N'dbo.PlatformPaymentSettings', N'SkipLandlordPhoneVerification') IS NOT NULL
                BEGIN
                    DECLARE @constraintName sysname;
                    SELECT @constraintName = dc.name FROM sys.default_constraints dc
                    INNER JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
                    WHERE dc.parent_object_id = OBJECT_ID(N'dbo.PlatformPaymentSettings') AND c.name = N'SkipLandlordPhoneVerification';
                    IF @constraintName IS NOT NULL
                    BEGIN
                        -- EXEC accepts a variable, not a concatenation containing a function call.
                        DECLARE @dropConstraintSql nvarchar(max) =
                            N'ALTER TABLE [dbo].[PlatformPaymentSettings] DROP CONSTRAINT ' + QUOTENAME(@constraintName);
                        EXEC sp_executesql @dropConstraintSql;
                    END;
                    ALTER TABLE [dbo].[PlatformPaymentSettings] DROP COLUMN [SkipLandlordPhoneVerification];
                END;");

            migrationBuilder.AddColumn<bool>(
                name: "WhatsAppRequested",
                table: "RentReminders",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<string>(
                name: "WhatsAppPhoneNumber",
                table: "AspNetUsers",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedWhatsAppPhoneNumber",
                table: "AspNetUsers",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingWhatsAppPhoneNumber",
                table: "AspNetUsers",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UsePrimaryPhoneForWhatsApp",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "WhatsAppEnabled",
                table: "ApartmentRentReminderRules",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "NotificationDeliveries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Channel = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    RelatedEntityId = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    TemplateName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TemplateLanguage = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RecipientUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RecipientPhoneNumberE164 = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReadAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    LastErrorCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    ButtonPayloadsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationDeliveries_AspNetUsers_RecipientUserId",
                        column: x => x.RecipientUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserCommunicationConsents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PhoneNumberE164 = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    TextVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserCommunicationConsents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserCommunicationConsents_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_NormalizedWhatsAppPhoneNumber",
                table: "AspNetUsers",
                column: "NormalizedWhatsAppPhoneNumber",
                unique: true,
                filter: "[NormalizedWhatsAppPhoneNumber] IS NOT NULL AND [IsWhatsAppPhoneVerified] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_IdempotencyKey",
                table: "NotificationDeliveries",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_ProviderMessageId",
                table: "NotificationDeliveries",
                column: "ProviderMessageId",
                filter: "[ProviderMessageId] IS NOT NULL AND [ProviderMessageId] <> N''");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_RecipientUserId",
                table: "NotificationDeliveries",
                column: "RecipientUserId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_Status_CreatedAt",
                table: "NotificationDeliveries",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UserCommunicationConsents_UserId_Channel_Purpose",
                table: "UserCommunicationConsents",
                columns: new[] { "UserId", "Channel", "Purpose" },
                unique: true,
                filter: "[Status] = N'Granted'");

            migrationBuilder.CreateIndex(
                name: "IX_UserCommunicationConsents_UserId_Channel_Purpose_Status",
                table: "UserCommunicationConsents",
                columns: new[] { "UserId", "Channel", "Purpose", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationDeliveries");

            migrationBuilder.DropTable(
                name: "UserCommunicationConsents");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_NormalizedWhatsAppPhoneNumber",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "WhatsAppRequested",
                table: "RentReminders");

            migrationBuilder.DropColumn(
                name: "NormalizedWhatsAppPhoneNumber",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PendingWhatsAppPhoneNumber",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "UsePrimaryPhoneForWhatsApp",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "WhatsAppEnabled",
                table: "ApartmentRentReminderRules");

            migrationBuilder.AddColumn<bool>(
                name: "SkipLandlordPhoneVerification",
                table: "PlatformPaymentSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<string>(
                name: "WhatsAppPhoneNumber",
                table: "AspNetUsers",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(16)",
                oldMaxLength: 16,
                oldNullable: true);
        }
    }
}
