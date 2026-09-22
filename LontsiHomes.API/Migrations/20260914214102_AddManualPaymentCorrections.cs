using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LontsiHomes.API.Migrations
{
    /// <inheritdoc />
    public partial class AddManualPaymentCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CorrectionJson",
                table: "Payments",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CorrectionLandlordNotifiedAt",
                table: "Payments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrectionRequestId",
                table: "Payments",
                type: "nvarchar(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CorrectionTenantNotifiedAt",
                table: "Payments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplacementPaymentId",
                table: "Payments",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CorrectionRequestId",
                table: "Payments",
                column: "CorrectionRequestId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_CorrectionRequestId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "CorrectionJson",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "CorrectionLandlordNotifiedAt",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "CorrectionRequestId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "CorrectionTenantNotifiedAt",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ReplacementPaymentId",
                table: "Payments");
        }
    }
}
