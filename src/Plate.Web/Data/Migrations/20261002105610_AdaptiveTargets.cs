using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Plate.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdaptiveTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaintenanceEstimates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    CalculatedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    EstimatedTdeeKcal = table.Column<int>(type: "integer", nullable: false),
                    TargetKcal = table.Column<int>(type: "integer", nullable: true),
                    TargetProteinG = table.Column<int>(type: "integer", nullable: true),
                    TrendWeightKg = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    DaysOfData = table.Column<int>(type: "integer", nullable: false),
                    LoggingCompleteness = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: false),
                    Method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LowConfidence = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceEstimates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceEstimates_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceEstimates_UserId_CalculatedOn",
                table: "MaintenanceEstimates",
                columns: new[] { "UserId", "CalculatedOn" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaintenanceEstimates");
        }
    }
}
