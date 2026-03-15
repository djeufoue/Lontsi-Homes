using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentHub.API.Migrations
{
    public partial class RemoveTenancyTenantForeignKey : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Tenancies_AspNetUsers_TenantId')
BEGIN
    ALTER TABLE [Tenancies] DROP CONSTRAINT [FK_Tenancies_AspNetUsers_TenantId];
END
");

            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Tenancies_TenantId' AND object_id = OBJECT_ID('[Tenancies]'))
BEGIN
    DROP INDEX [IX_Tenancies_TenantId] ON [Tenancies];
END
");

            migrationBuilder.Sql(@"
IF COL_LENGTH('Tenancies', 'TenantId') IS NOT NULL
BEGIN
    ALTER TABLE [Tenancies] DROP COLUMN [TenantId];
END
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "Tenancies",
                type: "nvarchar(450)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Tenancies_TenantId",
                table: "Tenancies",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_Tenancies_AspNetUsers_TenantId",
                table: "Tenancies",
                column: "TenantId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
