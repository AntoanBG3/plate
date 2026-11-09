using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Plate.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ChangelogSeenAt",
                table: "Profiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ParseRunId",
                table: "FoodLog",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ErrorEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Fingerprint = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Origin = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Path = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Stack = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    LastTraceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FirstVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    LastVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErrorEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ParseRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    AiModel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    MatchModel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ParserMs = table.Column<int>(type: "integer", nullable: false),
                    MatchMs = table.Column<int>(type: "integer", nullable: false),
                    ParserError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Parsed = table.Column<string>(type: "jsonb", nullable: false),
                    Resolved = table.Column<string>(type: "jsonb", nullable: false),
                    Saved = table.Column<string>(type: "jsonb", nullable: true),
                    Corrections = table.Column<int>(type: "integer", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParseRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ParseRuns_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Feedback",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Area = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Rating = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    Reasons = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Context = table.Column<string>(type: "jsonb", nullable: false),
                    ParseRunId = table.Column<int>(type: "integer", nullable: true),
                    ParseItemIndex = table.Column<int>(type: "integer", nullable: true),
                    ErrorEventId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Reply = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    StatusChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FixedInVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ReplySeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Feedback", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Feedback_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Feedback_ErrorEvents_ErrorEventId",
                        column: x => x.ErrorEventId,
                        principalTable: "ErrorEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Feedback_ParseRuns_ParseRunId",
                        column: x => x.ParseRunId,
                        principalTable: "ParseRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Changelog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TextEn = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    TextBg = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    FeedbackId = table.Column<int>(type: "integer", nullable: true),
                    Version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Changelog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Changelog_Feedback_FeedbackId",
                        column: x => x.FeedbackId,
                        principalTable: "Feedback",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "FeedbackAttachments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FeedbackId = table.Column<int>(type: "integer", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    Bytes = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedbackAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FeedbackAttachments_Feedback_FeedbackId",
                        column: x => x.FeedbackId,
                        principalTable: "Feedback",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FoodLog_ParseRunId",
                table: "FoodLog",
                column: "ParseRunId");

            migrationBuilder.CreateIndex(
                name: "IX_Changelog_CreatedAt",
                table: "Changelog",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Changelog_FeedbackId",
                table: "Changelog",
                column: "FeedbackId");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorEvents_Fingerprint",
                table: "ErrorEvents",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ErrorEvents_LastSeenAt",
                table: "ErrorEvents",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_Feedback_ClientId",
                table: "Feedback",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Feedback_ErrorEventId",
                table: "Feedback",
                column: "ErrorEventId");

            migrationBuilder.CreateIndex(
                name: "IX_Feedback_ParseRunId",
                table: "Feedback",
                column: "ParseRunId");

            migrationBuilder.CreateIndex(
                name: "IX_Feedback_Status",
                table: "Feedback",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Feedback_UserId_CreatedAt",
                table: "Feedback",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackAttachments_FeedbackId",
                table: "FeedbackAttachments",
                column: "FeedbackId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ParseRuns_CreatedAt",
                table: "ParseRuns",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ParseRuns_UserId_CreatedAt",
                table: "ParseRuns",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_FoodLog_ParseRuns_ParseRunId",
                table: "FoodLog",
                column: "ParseRunId",
                principalTable: "ParseRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FoodLog_ParseRuns_ParseRunId",
                table: "FoodLog");

            migrationBuilder.DropTable(
                name: "Changelog");

            migrationBuilder.DropTable(
                name: "FeedbackAttachments");

            migrationBuilder.DropTable(
                name: "Feedback");

            migrationBuilder.DropTable(
                name: "ErrorEvents");

            migrationBuilder.DropTable(
                name: "ParseRuns");

            migrationBuilder.DropIndex(
                name: "IX_FoodLog_ParseRunId",
                table: "FoodLog");

            migrationBuilder.DropColumn(
                name: "ChangelogSeenAt",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "ParseRunId",
                table: "FoodLog");
        }
    }
}
