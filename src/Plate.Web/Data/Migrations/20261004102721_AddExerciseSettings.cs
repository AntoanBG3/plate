using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plate.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExerciseSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "BarWeightKg",
                table: "Profiles",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 20m);

            migrationBuilder.AddColumn<List<decimal>>(
                name: "PlatesKg",
                table: "Profiles",
                type: "numeric[]",
                nullable: false,
                defaultValueSql: "'{25,20,15,10,5,2.5,1.25}'::numeric[]");

            migrationBuilder.CreateTable(
                name: "ExerciseSettings",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ExerciseId = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    BarWeightKg = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExerciseSettings", x => new { x.UserId, x.ExerciseId });
                    table.ForeignKey(
                        name: "FK_ExerciseSettings_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExerciseSettings_Exercises_ExerciseId",
                        column: x => x.ExerciseId,
                        principalTable: "Exercises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExerciseSettings_ExerciseId",
                table: "ExerciseSettings",
                column: "ExerciseId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExerciseSettings");

            migrationBuilder.DropColumn(
                name: "BarWeightKg",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "PlatesKg",
                table: "Profiles");
        }
    }
}
