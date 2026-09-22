using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using LontsiHomes.API.Data;

#nullable disable

namespace LontsiHomes.API.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260816050000_AddAutomaticPaymentControls")]
    public partial class AddAutomaticPaymentControls : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutomaticPaymentsEnabled",
                table: "Properties",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PlatformPaymentSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    AutomaticPaymentsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformPaymentSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "PlatformPaymentSettings",
                columns: new[] { "Id", "AutomaticPaymentsEnabled", "UpdatedAt", "UpdatedBy" },
                columnTypes: new[] { "int", "bit", "datetimeoffset", "nvarchar(max)" },
                values: new object[] { 1, false, new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero), null });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PlatformPaymentSettings");
            migrationBuilder.DropColumn(name: "AutomaticPaymentsEnabled", table: "Properties");
        }
    }
}
