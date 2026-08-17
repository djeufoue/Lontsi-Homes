using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RentHub.API.Data;

#nullable disable

namespace RentHub.API.Migrations;

/// <summary>
/// Adds scoped manager permissions without touching any existing business data.
/// Existing assignments are initialized with the safe read-only permission set.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20260817020000_AddManagerGranularPermissions")]
public partial class AddManagerGranularPermissions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        const long readOnlyPermissions = 744396316838953L;

        migrationBuilder.AddColumn<bool>(
            name: "AccessAllApartments",
            table: "PropertyManagerAssignments",
            type: "bit",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<long>(
            name: "PermissionFlags",
            table: "PropertyManagerAssignments",
            type: "bigint",
            nullable: false,
            defaultValue: readOnlyPermissions);

        // Existing managers keep access to their assigned property and apartments, but
        // all write operations must now be granted explicitly by the landlord.
        migrationBuilder.Sql($"""
            UPDATE [PropertyManagerAssignments]
            SET [AccessAllApartments] = 1,
                [PermissionFlags] = {readOnlyPermissions},
                [Permission] = 0;
            """);

        migrationBuilder.CreateTable(
            name: "ManagerApartmentPermissionOverrides",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                PropertyManagerAssignmentId = table.Column<int>(type: "int", nullable: false),
                ApartmentId = table.Column<int>(type: "int", nullable: false),
                HasAccess = table.Column<bool>(type: "bit", nullable: false),
                AllowedPermissionFlags = table.Column<long>(type: "bigint", nullable: false),
                DeniedPermissionFlags = table.Column<long>(type: "bigint", nullable: false),
                CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ManagerApartmentPermissionOverrides", x => x.Id);
                table.ForeignKey(
                    name: "FK_ManagerApartmentPermissionOverrides_Apartments_ApartmentId",
                    column: x => x.ApartmentId,
                    principalTable: "Apartments",
                    principalColumn: "Id");
                table.ForeignKey(
                    name: "FK_ManagerApartmentPermissionOverrides_PropertyManagerAssignments_PropertyManagerAssignmentId",
                    column: x => x.PropertyManagerAssignmentId,
                    principalTable: "PropertyManagerAssignments",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ManagerPermissionAuditLogs",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                PropertyId = table.Column<int>(type: "int", nullable: false),
                PropertyManagerAssignmentId = table.Column<int>(type: "int", nullable: false),
                ManagerId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                ChangedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                ChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                PreviousPermissionFlags = table.Column<long>(type: "bigint", nullable: false),
                NewPermissionFlags = table.Column<long>(type: "bigint", nullable: false),
                PreviousAccessAllApartments = table.Column<bool>(type: "bit", nullable: false),
                NewAccessAllApartments = table.Column<bool>(type: "bit", nullable: false),
                Details = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ManagerPermissionAuditLogs", x => x.Id);
                table.ForeignKey(
                    name: "FK_ManagerPermissionAuditLogs_PropertyManagerAssignments_PropertyManagerAssignmentId",
                    column: x => x.PropertyManagerAssignmentId,
                    principalTable: "PropertyManagerAssignments",
                    principalColumn: "Id");
            });

        migrationBuilder.CreateIndex(
            name: "IX_ManagerApartmentPermissionOverrides_ApartmentId",
            table: "ManagerApartmentPermissionOverrides",
            column: "ApartmentId");

        migrationBuilder.CreateIndex(
            name: "IX_ManagerApartmentPermissionOverrides_PropertyManagerAssignmentId_ApartmentId",
            table: "ManagerApartmentPermissionOverrides",
            columns: new[] { "PropertyManagerAssignmentId", "ApartmentId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ManagerPermissionAuditLogs_PropertyId_ManagerId_ChangedAt",
            table: "ManagerPermissionAuditLogs",
            columns: new[] { "PropertyId", "ManagerId", "ChangedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_ManagerPermissionAuditLogs_PropertyManagerAssignmentId",
            table: "ManagerPermissionAuditLogs",
            column: "PropertyManagerAssignmentId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ManagerApartmentPermissionOverrides");
        migrationBuilder.DropTable(name: "ManagerPermissionAuditLogs");

        migrationBuilder.DropColumn(name: "AccessAllApartments", table: "PropertyManagerAssignments");
        migrationBuilder.DropColumn(name: "PermissionFlags", table: "PropertyManagerAssignments");
    }
}
