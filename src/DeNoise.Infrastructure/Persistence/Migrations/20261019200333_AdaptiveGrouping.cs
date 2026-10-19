using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeNoise.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdaptiveGrouping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "adopted_count",
                schema: "alert",
                table: "alert_group",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "continued_from_group_id",
                schema: "alert",
                table: "alert_group",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "digest_last_at",
                schema: "alert",
                table: "alert_group",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "digest_next_at",
                schema: "alert",
                table: "alert_group",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "extension_count",
                schema: "alert",
                table: "alert_group",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_member_at",
                schema: "alert",
                table: "alert_group",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "window_hard_end_at",
                schema: "alert",
                table: "alert_group",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Groups that exist already keep their fixed window: the hard end is the current end, the last member joined when the group opened.
            migrationBuilder.Sql("UPDATE alert.alert_group SET window_hard_end_at = window_ends_at, last_member_at = opened_at");

            migrationBuilder.CreateIndex(
                name: "alert_group_digest_idx",
                schema: "alert",
                table: "alert_group",
                column: "digest_next_at",
                filter: "digest_next_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "alert_group_digest_idx",
                schema: "alert",
                table: "alert_group");

            migrationBuilder.DropColumn(
                name: "adopted_count",
                schema: "alert",
                table: "alert_group");

            migrationBuilder.DropColumn(
                name: "continued_from_group_id",
                schema: "alert",
                table: "alert_group");

            migrationBuilder.DropColumn(
                name: "digest_last_at",
                schema: "alert",
                table: "alert_group");

            migrationBuilder.DropColumn(
                name: "digest_next_at",
                schema: "alert",
                table: "alert_group");

            migrationBuilder.DropColumn(
                name: "extension_count",
                schema: "alert",
                table: "alert_group");

            migrationBuilder.DropColumn(
                name: "last_member_at",
                schema: "alert",
                table: "alert_group");

            migrationBuilder.DropColumn(
                name: "window_hard_end_at",
                schema: "alert",
                table: "alert_group");
        }
    }
}
