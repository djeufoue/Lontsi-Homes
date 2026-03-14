using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentHub.API.Migrations
{
    public partial class AddApartmentReminderSettings : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LeaseTerminationReminderDaysBeforeEnd",
                table: "Apartments",
                type: "int",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "RentReminderDaysBeforeDue",
                table: "Apartments",
                type: "int",
                nullable: false,
                defaultValue: 10);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LeaseTerminationReminderDaysBeforeEnd",
                table: "Apartments");

            migrationBuilder.DropColumn(
                name: "RentReminderDaysBeforeDue",
                table: "Apartments");
        }
    }
}
