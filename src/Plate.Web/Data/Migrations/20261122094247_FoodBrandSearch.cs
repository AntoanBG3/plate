using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plate.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class FoodBrandSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_FoodItems_Brand",
                table: "FoodItems",
                column: "Brand")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FoodItems_Brand",
                table: "FoodItems");
        }
    }
}
