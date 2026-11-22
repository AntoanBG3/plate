using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plate.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class FoodUnits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every food, entry and saved item so far was counted in grams (SPEC 4).
            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "SavedMealItems",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "Grams");

            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "FoodLog",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "Grams");

            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "FoodItems",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "Grams");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Unit",
                table: "SavedMealItems");

            migrationBuilder.DropColumn(
                name: "Unit",
                table: "FoodLog");

            migrationBuilder.DropColumn(
                name: "Unit",
                table: "FoodItems");
        }
    }
}
