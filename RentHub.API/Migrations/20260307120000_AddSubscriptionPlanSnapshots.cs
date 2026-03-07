using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentHub.API.Migrations
{
    public partial class AddSubscriptionPlanSnapshots : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PlanDurationInDaysSnapshot",
                table: "UserSubscriptions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int?>(
                name: "PlanMaxApartmentsPerPropertySnapshot",
                table: "UserSubscriptions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int?>(
                name: "PlanMaxPropertiesSnapshot",
                table: "UserSubscriptions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlanNameSnapshot",
                table: "UserSubscriptions",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "PlanPriceSnapshot",
                table: "UserSubscriptions",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.Sql(@"
UPDATE us
SET
    us.PlanNameSnapshot = ISNULL(sp.Name, ''),
    us.PlanPriceSnapshot = ISNULL(sp.Price, 0),
    us.PlanDurationInDaysSnapshot = ISNULL(sp.DurationInDays, 0),
    us.PlanMaxPropertiesSnapshot = sp.MaxProperties,
    us.PlanMaxApartmentsPerPropertySnapshot = sp.MaxApartmentsPerProperty
FROM UserSubscriptions us
LEFT JOIN SubscriptionPlans sp ON sp.Id = us.SubscriptionPlanId;
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlanDurationInDaysSnapshot",
                table: "UserSubscriptions");

            migrationBuilder.DropColumn(
                name: "PlanMaxApartmentsPerPropertySnapshot",
                table: "UserSubscriptions");

            migrationBuilder.DropColumn(
                name: "PlanMaxPropertiesSnapshot",
                table: "UserSubscriptions");

            migrationBuilder.DropColumn(
                name: "PlanNameSnapshot",
                table: "UserSubscriptions");

            migrationBuilder.DropColumn(
                name: "PlanPriceSnapshot",
                table: "UserSubscriptions");
        }
    }
}
