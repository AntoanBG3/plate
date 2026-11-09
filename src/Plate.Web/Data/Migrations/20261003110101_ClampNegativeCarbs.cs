using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plate.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClampNegativeCarbs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // USDA computes carbs "by difference", which dips just below zero for some meats and fish.
            migrationBuilder.Sql("""UPDATE "FoodItems" SET "CarbsPer100g" = 0 WHERE "CarbsPer100g" < 0;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
