using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plate.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class FoodPieceAndPackWeights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PackGrams",
                table: "FoodItems",
                type: "numeric(7,1)",
                precision: 7,
                scale: 1,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PieceGrams",
                table: "FoodItems",
                type: "numeric(7,1)",
                precision: 7,
                scale: 1,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ValuesEstimated",
                table: "FoodItems",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PackGrams",
                table: "FoodItems");

            migrationBuilder.DropColumn(
                name: "PieceGrams",
                table: "FoodItems");

            migrationBuilder.DropColumn(
                name: "ValuesEstimated",
                table: "FoodItems");
        }
    }
}
