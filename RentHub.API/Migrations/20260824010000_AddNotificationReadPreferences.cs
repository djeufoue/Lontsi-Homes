using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentHub.API.Migrations
{
    [Migration("20260824010000_AddNotificationReadPreferences")]
    public partial class AddNotificationReadPreferences : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ConversationEmailNotificationsEnabled",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DecisionViewedAt",
                table: "TenancyExtensionRequests",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DecisionViewedAt",
                table: "TenancyTerminationRequests",
                type: "datetimeoffset",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConversationEmailNotificationsEnabled",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DecisionViewedAt",
                table: "TenancyExtensionRequests");

            migrationBuilder.DropColumn(
                name: "DecisionViewedAt",
                table: "TenancyTerminationRequests");
        }
    }
}
