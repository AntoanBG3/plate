using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plate.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWarmupSlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WarmupSlots",
                table: "SessionExercises",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Sessions logged before this kept warm-up rows only on the page; count the ones that were logged.
            migrationBuilder.Sql("""
                UPDATE "SessionExercises" e
                SET "WarmupSlots" = (SELECT count(*) FROM "Sets" s WHERE s."SessionExerciseId" = e."Id" AND s."IsWarmup" AND s."DeletedAt" IS NULL)
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WarmupSlots",
                table: "SessionExercises");
        }
    }
}
