using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GymTracker.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class UsualFoods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UsualFoods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Phrase = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FoodItemId = table.Column<int>(type: "integer", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsualFoods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UsualFoods_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UsualFoods_FoodItems_FoodItemId",
                        column: x => x.FoodItemId,
                        principalTable: "FoodItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FoodLog_UserId_FoodItemId_CreatedAt",
                table: "FoodLog",
                columns: new[] { "UserId", "FoodItemId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UsualFoods_FoodItemId",
                table: "UsualFoods",
                column: "FoodItemId");

            migrationBuilder.CreateIndex(
                name: "IX_UsualFoods_UserId_Phrase",
                table: "UsualFoods",
                columns: new[] { "UserId", "Phrase" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UsualFoods");

            migrationBuilder.DropIndex(
                name: "IX_FoodLog_UserId_FoodItemId_CreatedAt",
                table: "FoodLog");
        }
    }
}
