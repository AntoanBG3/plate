using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymTracker.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOnboardingChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OnboardingChats",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Draft = table.Column<string>(type: "jsonb", nullable: false),
                    Messages = table.Column<string>(type: "jsonb", nullable: false),
                    Rejected = table.Column<string>(type: "jsonb", nullable: false),
                    QuickReplies = table.Column<string>(type: "jsonb", nullable: false),
                    Turns = table.Column<int>(type: "integer", nullable: false),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnboardingChats", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_OnboardingChats_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OnboardingChats");
        }
    }
}
