using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentHub.API.Migrations
{
    /// <inheritdoc />
    public partial class RemoveApartmentFloorUpperLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Apartments_FloorNumber",
                table: "Apartments");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Apartments_FloorNumber",
                table: "Apartments",
                sql: "[FloorNumber] >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Apartments_FloorNumber",
                table: "Apartments");

            migrationBuilder.Sql("UPDATE [Apartments] SET [FloorNumber] = 30 WHERE [FloorNumber] > 30;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Apartments_FloorNumber",
                table: "Apartments",
                sql: "[FloorNumber] >= 0 AND [FloorNumber] <= 30");
        }
    }
}
