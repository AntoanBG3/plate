using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymTracker.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AutoProtein : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "ProteinGramsPerKg",
                table: "Profiles",
                type: "numeric(4,2)",
                precision: 4,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(4,2)",
                oldPrecision: 4,
                oldScale: 2);

            // 2.0 was the old default for everyone; it becomes Auto (SPEC 5.6). Any other value was chosen and stays.
            migrationBuilder.Sql("UPDATE \"Profiles\" SET \"ProteinGramsPerKg\" = NULL WHERE \"ProteinGramsPerKg\" = 2.0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"Profiles\" SET \"ProteinGramsPerKg\" = 2.0 WHERE \"ProteinGramsPerKg\" IS NULL;");

            migrationBuilder.AlterColumn<decimal>(
                name: "ProteinGramsPerKg",
                table: "Profiles",
                type: "numeric(4,2)",
                precision: 4,
                scale: 2,
                nullable: false,
                defaultValue: 2.0m,
                oldClrType: typeof(decimal),
                oldType: "numeric(4,2)",
                oldPrecision: 4,
                oldScale: 2,
                oldNullable: true);
        }
    }
}
