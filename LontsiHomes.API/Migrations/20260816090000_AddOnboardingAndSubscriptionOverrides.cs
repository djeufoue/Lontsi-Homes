using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using LontsiHomes.API.Data;

#nullable disable

namespace LontsiHomes.API.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260816090000_AddOnboardingAndSubscriptionOverrides")]
    public partial class AddOnboardingAndSubscriptionOverrides : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SkipLandlordPhoneVerification",
                table: "PlatformPaymentSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsSubscriptionExempt",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SkipLandlordPhoneVerification",
                table: "PlatformPaymentSettings");

            migrationBuilder.DropColumn(
                name: "IsSubscriptionExempt",
                table: "AspNetUsers");
        }
    }
}
