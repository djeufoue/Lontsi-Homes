using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using LontsiHomes.API.Data;

#nullable disable

namespace LontsiHomes.API.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260819090000_AddSubscriptionInquiryCommitmentMonths")]
    public partial class AddSubscriptionInquiryCommitmentMonths : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CommitmentMonths",
                table: "SubscriptionInquiries",
                type: "int",
                nullable: false,
                defaultValue: 6);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CommitmentMonths",
                table: "SubscriptionInquiries");
        }
    }
}
