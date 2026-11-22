using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plate.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ErrorEventDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ErrorEventDays",
                columns: table => new
                {
                    ErrorEventId = table.Column<int>(type: "integer", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErrorEventDays", x => new { x.ErrorEventId, x.Day });
                    table.ForeignKey(
                        name: "FK_ErrorEventDays_ErrorEvents_ErrorEventId",
                        column: x => x.ErrorEventId,
                        principalTable: "ErrorEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ErrorEventDays_Day",
                table: "ErrorEventDays",
                column: "Day");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ErrorEventDays");
        }
    }
}
