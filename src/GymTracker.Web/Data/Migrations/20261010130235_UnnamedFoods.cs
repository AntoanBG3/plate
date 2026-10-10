using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymTracker.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class UnnamedFoods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Unnamed",
                table: "FoodItems",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Open Food Facts products cached without a name were called "Product <barcode>" (feedback #5). They take
            // their brand for now and are marked, so the next scan asks for a name; entries logged under the old
            // stand-in follow.
            migrationBuilder.Sql("""
                UPDATE "FoodLog" e SET "Description" = f."Brand"
                FROM "FoodItems" f
                WHERE e."FoodItemId" = f."Id" AND f."Source" = 'OpenFoodFacts' AND f."Brand" IS NOT NULL
                  AND f."Name" = 'Product ' || f."Barcode" AND e."Description" = f."Name" || ' (' || f."Brand" || ')';
                UPDATE "FoodItems" SET "Unnamed" = TRUE, "Name" = COALESCE("Brand", "Name")
                WHERE "Source" = 'OpenFoodFacts' AND "Name" = 'Product ' || "Barcode";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Unnamed",
                table: "FoodItems");
        }
    }
}
