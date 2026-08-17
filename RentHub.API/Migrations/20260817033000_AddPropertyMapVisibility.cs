using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RentHub.API.Data;

#nullable disable

namespace RentHub.API.Migrations;

/// <summary>
/// Adds the administrator-controlled property map feature switch.
/// Existing and new properties start with the map disabled.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20260817033000_AddPropertyMapVisibility")]
public partial class AddPropertyMapVisibility : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "MapEnabled",
            table: "Properties",
            type: "bit",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "MapEnabled",
            table: "Properties");
    }
}
