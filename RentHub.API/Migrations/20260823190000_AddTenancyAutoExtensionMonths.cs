using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentHub.API.Migrations
{
    [Migration("20260823190000_AddTenancyAutoExtensionMonths")]
    public partial class AddTenancyAutoExtensionMonths : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AutoExtensionMonths",
                table: "Tenancies",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.Sql(
                "UPDATE [Tenancies] SET [EndBehavior] = 3, [EndDate] = NULL WHERE [EndBehavior] = 2;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tenancies_AutoExtensionMonths",
                table: "Tenancies",
                sql: "[AutoExtensionMonths] >= 1 AND [AutoExtensionMonths] <= 12");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Tenancies_AutoExtensionMonths",
                table: "Tenancies");

            migrationBuilder.DropColumn(
                name: "AutoExtensionMonths",
                table: "Tenancies");
        }
    }
}
