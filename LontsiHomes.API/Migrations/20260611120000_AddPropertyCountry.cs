using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LontsiHomes.API.Migrations
{
    [Migration("20260611120000_AddPropertyCountry")]
    public partial class AddPropertyCountry : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('Properties', 'CountryIsoCode') IS NULL
BEGIN
    ALTER TABLE [Properties] ADD [CountryIsoCode] nvarchar(2) NULL;
END

IF COL_LENGTH('Properties', 'CountryCode') IS NULL
BEGIN
    ALTER TABLE [Properties] ADD [CountryCode] nvarchar(8) NULL;
END
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('Properties', 'CountryCode') IS NOT NULL
BEGIN
    ALTER TABLE [Properties] DROP COLUMN [CountryCode];
END

IF COL_LENGTH('Properties', 'CountryIsoCode') IS NOT NULL
BEGIN
    ALTER TABLE [Properties] DROP COLUMN [CountryIsoCode];
END
");
        }
    }
}
