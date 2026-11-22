using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plate.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ParseRunTotalTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NamerMs",
                table: "ParseRuns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TotalMs",
                table: "ParseRuns",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NamerMs",
                table: "ParseRuns");

            migrationBuilder.DropColumn(
                name: "TotalMs",
                table: "ParseRuns");
        }
    }
}
