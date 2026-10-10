using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymTracker.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class LabelPhotoSlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LabelScanPhotos_LabelScanId",
                table: "LabelScanPhotos");

            migrationBuilder.AddColumn<int>(
                name: "Position",
                table: "LabelScanPhotos",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_LabelScanPhotos_LabelScanId_Position",
                table: "LabelScanPhotos",
                columns: new[] { "LabelScanId", "Position" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The old schema allowed one photo per scan; keep the first.
            migrationBuilder.Sql("""DELETE FROM "LabelScanPhotos" WHERE "Position" > 0;""");

            migrationBuilder.DropIndex(
                name: "IX_LabelScanPhotos_LabelScanId_Position",
                table: "LabelScanPhotos");

            migrationBuilder.DropColumn(
                name: "Position",
                table: "LabelScanPhotos");

            migrationBuilder.CreateIndex(
                name: "IX_LabelScanPhotos_LabelScanId",
                table: "LabelScanPhotos",
                column: "LabelScanId",
                unique: true);
        }
    }
}
