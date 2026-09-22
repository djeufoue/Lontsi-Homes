using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LontsiHomes.API.Migrations
{
    public partial class RemoveTenancyApartmentShadowColumn : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Tenancies_Apartments_ApartmentId1')
BEGIN
    ALTER TABLE [Tenancies] DROP CONSTRAINT [FK_Tenancies_Apartments_ApartmentId1];
END
");

            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Tenancies_ApartmentId1' AND object_id = OBJECT_ID('[Tenancies]'))
BEGIN
    DROP INDEX [IX_Tenancies_ApartmentId1] ON [Tenancies];
END
");

            migrationBuilder.Sql(@"
IF COL_LENGTH('Tenancies', 'ApartmentId1') IS NOT NULL
BEGIN
    ALTER TABLE [Tenancies] DROP COLUMN [ApartmentId1];
END
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ApartmentId1",
                table: "Tenancies",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenancies_ApartmentId1",
                table: "Tenancies",
                column: "ApartmentId1");

            migrationBuilder.AddForeignKey(
                name: "FK_Tenancies_Apartments_ApartmentId1",
                table: "Tenancies",
                column: "ApartmentId1",
                principalTable: "Apartments",
                principalColumn: "Id");
        }
    }
}
