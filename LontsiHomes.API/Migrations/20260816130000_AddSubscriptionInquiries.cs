using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata;
using LontsiHomes.API.Data;

#nullable disable

namespace LontsiHomes.API.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260816130000_AddSubscriptionInquiries")]
    public partial class AddSubscriptionInquiries : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SubscriptionInquiries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:ValueGenerationStrategy", SqlServerValueGenerationStrategy.IdentityColumn),
                    RequesterUserId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    RequesterName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    RequesterEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PlanName = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    PropertyCount = table.Column<int>(type: "int", nullable: false),
                    ApartmentCount = table.Column<int>(type: "int", nullable: false),
                    TenantCount = table.Column<int>(type: "int", nullable: false),
                    ProposedMonthlyPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PublicAccessToken = table.Column<string>(type: "nvarchar(96)", maxLength: 96, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastMessageAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RequesterLastReadAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AdminLastReadAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionInquiries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubscriptionInquiries_AspNetUsers_RequesterUserId",
                        column: x => x.RequesterUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionInquiryMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:ValueGenerationStrategy", SqlServerValueGenerationStrategy.IdentityColumn),
                    SubscriptionInquiryId = table.Column<int>(type: "int", nullable: false),
                    SenderUserId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    SenderName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    SenderEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SentByAdmin = table.Column<bool>(type: "bit", nullable: false),
                    Body = table.Column<string>(type: "nvarchar(3000)", maxLength: 3000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionInquiryMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubscriptionInquiryMessages_AspNetUsers_SenderUserId",
                        column: x => x.SenderUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SubscriptionInquiryMessages_SubscriptionInquiries_SubscriptionInquiryId",
                        column: x => x.SubscriptionInquiryId,
                        principalTable: "SubscriptionInquiries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionInquiries_PublicAccessToken",
                table: "SubscriptionInquiries",
                column: "PublicAccessToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionInquiries_RequesterUserId_LastMessageAt",
                table: "SubscriptionInquiries",
                columns: new[] { "RequesterUserId", "LastMessageAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionInquiryMessages_SenderUserId",
                table: "SubscriptionInquiryMessages",
                column: "SenderUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionInquiryMessages_SubscriptionInquiryId_CreatedAt",
                table: "SubscriptionInquiryMessages",
                columns: new[] { "SubscriptionInquiryId", "CreatedAt" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "SubscriptionInquiryMessages");
            migrationBuilder.DropTable(name: "SubscriptionInquiries");
        }
    }
}
