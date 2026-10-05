using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymTracker.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class DietBreak : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "CutCheckDismissedUntil",
                table: "Profiles",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "CutStartedOn",
                table: "Profiles",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DietBreakUntil",
                table: "Profiles",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CutCheckDismissedUntil",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "CutStartedOn",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "DietBreakUntil",
                table: "Profiles");
        }
    }
}
