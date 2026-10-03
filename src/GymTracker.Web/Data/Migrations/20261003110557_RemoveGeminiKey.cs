using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymTracker.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveGeminiKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Food parsing moved to OpenRouter; a saved Gemini key no longer does anything.
            migrationBuilder.Sql("""DELETE FROM "AppSettings" WHERE "Name" = 'Gemini:ApiKey';""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
