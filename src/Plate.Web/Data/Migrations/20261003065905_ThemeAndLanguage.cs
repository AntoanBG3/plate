using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plate.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ThemeAndLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing accounts keep English and get the new dark default.
            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "Profiles",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "English");

            migrationBuilder.AddColumn<string>(
                name: "Theme",
                table: "Profiles",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Dark");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Language",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "Theme",
                table: "Profiles");
        }
    }
}
