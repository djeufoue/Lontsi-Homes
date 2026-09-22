using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using LontsiHomes.API.Data;

#nullable disable

namespace LontsiHomes.API.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260824020000_AddConversationMessageReplies")]
    public partial class AddConversationMessageReplies : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReplyToMessageId",
                table: "ConversationMessages",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessages_ReplyToMessageId",
                table: "ConversationMessages",
                column: "ReplyToMessageId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConversationMessages_ConversationMessages_ReplyToMessageId",
                table: "ConversationMessages",
                column: "ReplyToMessageId",
                principalTable: "ConversationMessages",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConversationMessages_ConversationMessages_ReplyToMessageId",
                table: "ConversationMessages");

            migrationBuilder.DropIndex(
                name: "IX_ConversationMessages_ReplyToMessageId",
                table: "ConversationMessages");

            migrationBuilder.DropColumn(
                name: "ReplyToMessageId",
                table: "ConversationMessages");
        }
    }
}
