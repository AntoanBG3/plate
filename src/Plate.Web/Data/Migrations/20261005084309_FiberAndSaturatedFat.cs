using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plate.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class FiberAndSaturatedFat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "FiberG",
                table: "SavedMealItems",
                type: "numeric(6,1)",
                precision: 6,
                scale: 1,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SaturatedFatG",
                table: "SavedMealItems",
                type: "numeric(6,1)",
                precision: 6,
                scale: 1,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FiberG",
                table: "FoodLog",
                type: "numeric(6,1)",
                precision: 6,
                scale: 1,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SaturatedFatG",
                table: "FoodLog",
                type: "numeric(6,1)",
                precision: 6,
                scale: 1,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FiberPer100g",
                table: "FoodItems",
                type: "numeric(6,2)",
                precision: 6,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SaturatedFatPer100g",
                table: "FoodItems",
                type: "numeric(6,2)",
                precision: 6,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FiberG",
                table: "SavedMealItems");

            migrationBuilder.DropColumn(
                name: "SaturatedFatG",
                table: "SavedMealItems");

            migrationBuilder.DropColumn(
                name: "FiberG",
                table: "FoodLog");

            migrationBuilder.DropColumn(
                name: "SaturatedFatG",
                table: "FoodLog");

            migrationBuilder.DropColumn(
                name: "FiberPer100g",
                table: "FoodItems");

            migrationBuilder.DropColumn(
                name: "SaturatedFatPer100g",
                table: "FoodItems");
        }
    }
}
