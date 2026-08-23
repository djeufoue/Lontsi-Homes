using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentHub.API.Migrations
{
    [Migration("20260823235900_AddInternalPropertyTeamConversations")]
    public partial class AddInternalPropertyTeamConversations : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ApartmentConversations_ApartmentId_VisitorId",
                table: "ApartmentConversations");

            migrationBuilder.AddColumn<bool>(
                name: "IsPropertyTeamConversation",
                table: "ApartmentConversations",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_ApartmentConversations_ApartmentId_VisitorId_IsPropertyTeamConversation",
                table: "ApartmentConversations",
                columns: new[] { "ApartmentId", "VisitorId", "IsPropertyTeamConversation" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ApartmentConversations_ApartmentId_VisitorId_IsPropertyTeamConversation",
                table: "ApartmentConversations");

            migrationBuilder.DropColumn(
                name: "IsPropertyTeamConversation",
                table: "ApartmentConversations");

            migrationBuilder.CreateIndex(
                name: "IX_ApartmentConversations_ApartmentId_VisitorId",
                table: "ApartmentConversations",
                columns: new[] { "ApartmentId", "VisitorId" },
                unique: true);
        }
    }
}
