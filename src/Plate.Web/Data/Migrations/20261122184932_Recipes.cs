using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Plate.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Recipes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Recipes",
                columns: table => new
                {
                    FoodItemId = table.Column<int>(type: "integer", nullable: false),
                    YieldGrams = table.Column<decimal>(type: "numeric(7,1)", precision: 7, scale: 1, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recipes", x => x.FoodItemId);
                    table.ForeignKey(
                        name: "FK_Recipes_FoodItems_FoodItemId",
                        column: x => x.FoodItemId,
                        principalTable: "FoodItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecipeIngredients",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RecipeFoodItemId = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Grams = table.Column<decimal>(type: "numeric(7,1)", precision: 7, scale: 1, nullable: false),
                    FoodItemId = table.Column<int>(type: "integer", nullable: true),
                    KcalPer100g = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false),
                    ProteinPer100g = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    CarbsPer100g = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    FatPer100g = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    FiberPer100g = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    SaturatedFatPer100g = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeIngredients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecipeIngredients_FoodItems_FoodItemId",
                        column: x => x.FoodItemId,
                        principalTable: "FoodItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RecipeIngredients_Recipes_RecipeFoodItemId",
                        column: x => x.RecipeFoodItemId,
                        principalTable: "Recipes",
                        principalColumn: "FoodItemId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeIngredients_FoodItemId",
                table: "RecipeIngredients",
                column: "FoodItemId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeIngredients_RecipeFoodItemId",
                table: "RecipeIngredients",
                column: "RecipeFoodItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecipeIngredients");

            migrationBuilder.DropTable(
                name: "Recipes");
        }
    }
}
