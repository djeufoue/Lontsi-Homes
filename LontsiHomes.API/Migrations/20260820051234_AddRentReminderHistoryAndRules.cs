using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LontsiHomes.API.Migrations
{
    public partial class AddRentReminderHistoryAndRules : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ManualRentReminderCooldownHours",
                table: "Apartments",
                type: "int",
                nullable: false,
                defaultValue: 24);

            migrationBuilder.AddColumn<int>(
                name: "ManualRentReminderLimit",
                table: "Apartments",
                type: "int",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.CreateTable(
                name: "ApartmentRentReminderRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ApartmentId = table.Column<int>(type: "int", nullable: false),
                    Timing = table.Column<int>(type: "int", nullable: false),
                    Days = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    EmailEnabled = table.Column<bool>(type: "bit", nullable: false),
                    SmsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApartmentRentReminderRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApartmentRentReminderRules_Apartments_ApartmentId",
                        column: x => x.ApartmentId,
                        principalTable: "Apartments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RentReminders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenancyId = table.Column<int>(type: "int", nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    IsManual = table.Column<bool>(type: "bit", nullable: false),
                    ScheduledFor = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    OutstandingAmountSnapshot = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    IncludedPeriodCount = table.Column<int>(type: "int", nullable: false),
                    RecipientEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    RecipientPhone = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    PlainTextBody = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HtmlBody = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EmailStatus = table.Column<int>(type: "int", nullable: false),
                    SmsStatus = table.Column<int>(type: "int", nullable: false),
                    EmailAttemptCount = table.Column<int>(type: "int", nullable: false),
                    SmsAttemptCount = table.Column<int>(type: "int", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    RequestedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RentReminders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RentReminders_Tenancies_TenancyId",
                        column: x => x.TenancyId,
                        principalTable: "Tenancies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RentReminderPeriods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RentReminderId = table.Column<int>(type: "int", nullable: false),
                    RentPeriodId = table.Column<int>(type: "int", nullable: false),
                    Relation = table.Column<int>(type: "int", nullable: false),
                    IsTrigger = table.Column<bool>(type: "bit", nullable: false),
                    PeriodStartSnapshot = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PeriodEndSnapshot = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DueDateSnapshot = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AmountSnapshot = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    PaidAmountSnapshot = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    OutstandingAmountSnapshot = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RentReminderPeriods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RentReminderPeriods_RentPeriods_RentPeriodId",
                        column: x => x.RentPeriodId,
                        principalTable: "RentPeriods",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RentReminderPeriods_RentReminders_RentReminderId",
                        column: x => x.RentReminderId,
                        principalTable: "RentReminders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RentReminderTriggers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RentReminderId = table.Column<int>(type: "int", nullable: false),
                    RentPeriodId = table.Column<int>(type: "int", nullable: false),
                    ApartmentRentReminderRuleId = table.Column<int>(type: "int", nullable: true),
                    Category = table.Column<int>(type: "int", nullable: false),
                    ManualSequence = table.Column<int>(type: "int", nullable: false),
                    TriggerKey = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RentReminderTriggers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RentReminderTriggers_ApartmentRentReminderRules_ApartmentRentReminderRuleId",
                        column: x => x.ApartmentRentReminderRuleId,
                        principalTable: "ApartmentRentReminderRules",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RentReminderTriggers_RentPeriods_RentPeriodId",
                        column: x => x.RentPeriodId,
                        principalTable: "RentPeriods",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RentReminderTriggers_RentReminders_RentReminderId",
                        column: x => x.RentReminderId,
                        principalTable: "RentReminders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApartmentRentReminderRules_ApartmentId_Timing_Days",
                table: "ApartmentRentReminderRules",
                columns: new[] { "ApartmentId", "Timing", "Days" },
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_ApartmentRentReminderRules_IsEnabled_Timing_Days",
                table: "ApartmentRentReminderRules",
                columns: new[] { "IsEnabled", "Timing", "Days" });

            migrationBuilder.CreateIndex(
                name: "IX_RentReminders_Status_ScheduledFor",
                table: "RentReminders",
                columns: new[] { "Status", "ScheduledFor" });

            migrationBuilder.CreateIndex(
                name: "IX_RentReminders_TenancyId_SentAt",
                table: "RentReminders",
                columns: new[] { "TenancyId", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RentReminderPeriods_RentPeriodId",
                table: "RentReminderPeriods",
                column: "RentPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_RentReminderPeriods_RentReminderId_RentPeriodId",
                table: "RentReminderPeriods",
                columns: new[] { "RentReminderId", "RentPeriodId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RentReminderTriggers_ApartmentRentReminderRuleId",
                table: "RentReminderTriggers",
                column: "ApartmentRentReminderRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_RentReminderTriggers_RentPeriodId_Category",
                table: "RentReminderTriggers",
                columns: new[] { "RentPeriodId", "Category" });

            migrationBuilder.CreateIndex(
                name: "IX_RentReminderTriggers_RentReminderId",
                table: "RentReminderTriggers",
                column: "RentReminderId");

            migrationBuilder.CreateIndex(
                name: "IX_RentReminderTriggers_TriggerKey",
                table: "RentReminderTriggers",
                column: "TriggerKey",
                unique: true);

            migrationBuilder.Sql(
                """
                INSERT INTO [ApartmentRentReminderRules]
                    ([ApartmentId], [Timing], [Days], [IsEnabled], [EmailEnabled], [SmsEnabled], [SortOrder], [IsDeleted], [CreatedBy], [CreatedAt])
                SELECT [Id], 0, 10, 1, 1, 0, 0, 0, N'system:migration', SYSUTCDATETIME()
                FROM [Apartments]
                WHERE [IsDeleted] = 0;

                INSERT INTO [ApartmentRentReminderRules]
                    ([ApartmentId], [Timing], [Days], [IsEnabled], [EmailEnabled], [SmsEnabled], [SortOrder], [IsDeleted], [CreatedBy], [CreatedAt])
                SELECT [Id], 2, 5, 1, 1, 0, 1, 0, N'system:migration', SYSUTCDATETIME()
                FROM [Apartments]
                WHERE [IsDeleted] = 0;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "RentReminderPeriods");
            migrationBuilder.DropTable(name: "RentReminderTriggers");
            migrationBuilder.DropTable(name: "ApartmentRentReminderRules");
            migrationBuilder.DropTable(name: "RentReminders");

            migrationBuilder.DropColumn(name: "ManualRentReminderCooldownHours", table: "Apartments");
            migrationBuilder.DropColumn(name: "ManualRentReminderLimit", table: "Apartments");
        }
    }
}
