using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlertHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LifecycleAndCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "lifecycle_policy_id",
                schema: "alert",
                table: "episode",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "stale_since",
                schema: "alert",
                table: "episode",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "lifecycle_policy_id",
                schema: "alert",
                table: "episode");

            migrationBuilder.DropColumn(
                name: "stale_since",
                schema: "alert",
                table: "episode");
        }
    }
}
