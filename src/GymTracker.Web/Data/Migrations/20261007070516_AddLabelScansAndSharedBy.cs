using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GymTracker.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLabelScansAndSharedBy : Migration
    {
        /// <summary>
        /// Shared manual foods made before AddedByUserId existed are credited to whoever logged them first (SPEC 4).
        /// Soft-deleted entries count: they still show who logged first.
        /// </summary>
        public const string BackfillAddedBy = """
            UPDATE "FoodItems" f
            SET "AddedByUserId" = first."UserId"
            FROM (
                SELECT DISTINCT ON ("FoodItemId") "FoodItemId", "UserId"
                FROM "FoodLog"
                WHERE "FoodItemId" IS NOT NULL
                ORDER BY "FoodItemId", "CreatedAt"
            ) first
            WHERE f."Id" = first."FoodItemId" AND f."Source" = 'Manual' AND f."OwnerUserId" IS NULL AND f."AddedByUserId" IS NULL;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FoodItems_Barcode",
                table: "FoodItems");

            migrationBuilder.AddColumn<string>(
                name: "AddedByUserId",
                table: "FoodItems",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LabelScans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LatencyMs = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Reading = table.Column<string>(type: "jsonb", nullable: true),
                    Values = table.Column<string>(type: "jsonb", nullable: true),
                    FoodItemId = table.Column<int>(type: "integer", nullable: true),
                    Saved = table.Column<string>(type: "jsonb", nullable: true),
                    Edited = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabelScans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LabelScans_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LabelScans_FoodItems_FoodItemId",
                        column: x => x.FoodItemId,
                        principalTable: "FoodItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "LabelScanPhotos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LabelScanId = table.Column<int>(type: "integer", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    Bytes = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabelScanPhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LabelScanPhotos_LabelScans_LabelScanId",
                        column: x => x.LabelScanId,
                        principalTable: "LabelScans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FoodItems_AddedByUserId",
                table: "FoodItems",
                column: "AddedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FoodItems_Barcode_Shared",
                table: "FoodItems",
                column: "Barcode",
                unique: true,
                filter: "\"Barcode\" IS NOT NULL AND \"OwnerUserId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FoodItems_Owner_Barcode",
                table: "FoodItems",
                columns: new[] { "OwnerUserId", "Barcode" },
                unique: true,
                filter: "\"Barcode\" IS NOT NULL AND \"OwnerUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LabelScanPhotos_LabelScanId",
                table: "LabelScanPhotos",
                column: "LabelScanId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LabelScans_CreatedAt",
                table: "LabelScans",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_LabelScans_FoodItemId",
                table: "LabelScans",
                column: "FoodItemId");

            migrationBuilder.CreateIndex(
                name: "IX_LabelScans_UserId_CreatedAt",
                table: "LabelScans",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_FoodItems_AspNetUsers_AddedByUserId",
                table: "FoodItems",
                column: "AddedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.Sql(BackfillAddedBy);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FoodItems_AspNetUsers_AddedByUserId",
                table: "FoodItems");

            migrationBuilder.DropTable(
                name: "LabelScanPhotos");

            migrationBuilder.DropTable(
                name: "LabelScans");

            migrationBuilder.DropIndex(
                name: "IX_FoodItems_AddedByUserId",
                table: "FoodItems");

            migrationBuilder.DropIndex(
                name: "IX_FoodItems_Barcode_Shared",
                table: "FoodItems");

            migrationBuilder.DropIndex(
                name: "IX_FoodItems_Owner_Barcode",
                table: "FoodItems");

            migrationBuilder.DropColumn(
                name: "AddedByUserId",
                table: "FoodItems");

            migrationBuilder.CreateIndex(
                name: "IX_FoodItems_Barcode",
                table: "FoodItems",
                column: "Barcode",
                unique: true,
                filter: "\"Barcode\" IS NOT NULL");
        }
    }
}
