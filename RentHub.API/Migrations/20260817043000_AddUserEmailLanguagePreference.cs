using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RentHub.API.Data;

#nullable disable

namespace RentHub.API.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260817043000_AddUserEmailLanguagePreference")]
    public partial class AddUserEmailLanguagePreference : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EmailLanguage",
                table: "AspNetUsers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("UPDATE [AspNetUsers] SET [EmailLanguage] = [Language];");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailLanguage",
                table: "AspNetUsers");
        }
    }
}
