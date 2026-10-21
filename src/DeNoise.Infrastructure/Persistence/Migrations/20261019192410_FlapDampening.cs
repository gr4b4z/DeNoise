using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeNoise.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FlapDampening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "closure_hold_until",
                schema: "alert",
                table: "episode",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "flap_window_started_at",
                schema: "alert",
                table: "episode",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_flapping",
                schema: "alert",
                table: "episode",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "reopen_count",
                schema: "alert",
                table: "episode",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "episode_closure_hold_idx",
                schema: "alert",
                table: "episode",
                column: "closure_hold_until",
                filter: "closure_hold_until IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "episode_closure_hold_idx",
                schema: "alert",
                table: "episode");

            migrationBuilder.DropColumn(
                name: "closure_hold_until",
                schema: "alert",
                table: "episode");

            migrationBuilder.DropColumn(
                name: "flap_window_started_at",
                schema: "alert",
                table: "episode");

            migrationBuilder.DropColumn(
                name: "is_flapping",
                schema: "alert",
                table: "episode");

            migrationBuilder.DropColumn(
                name: "reopen_count",
                schema: "alert",
                table: "episode");
        }
    }
}
