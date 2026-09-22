using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using LontsiHomes.API.Data;

#nullable disable

namespace LontsiHomes.API.Migrations;

/// <summary>
/// Persists feedback-dialog preferences on the property so every authorized user
/// observes the same configuration across browsers and sessions.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20260817053000_AddPropertyDialogPreferences")]
public partial class AddPropertyDialogPreferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "SuccessDialogAutoCloseEnabled",
            table: "Properties",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<int>(
            name: "SuccessDialogAutoCloseSeconds",
            table: "Properties",
            type: "int",
            nullable: false,
            defaultValue: 5);

        migrationBuilder.AddColumn<string>(
            name: "SuccessDialogPosition",
            table: "Properties",
            type: "nvarchar(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "bottom-center");

        migrationBuilder.AddColumn<bool>(
            name: "SuccessDialogShowSuccessMessages",
            table: "Properties",
            type: "bit",
            nullable: false,
            defaultValue: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "SuccessDialogAutoCloseEnabled",
            table: "Properties");

        migrationBuilder.DropColumn(
            name: "SuccessDialogAutoCloseSeconds",
            table: "Properties");

        migrationBuilder.DropColumn(
            name: "SuccessDialogPosition",
            table: "Properties");

        migrationBuilder.DropColumn(
            name: "SuccessDialogShowSuccessMessages",
            table: "Properties");
    }
}
