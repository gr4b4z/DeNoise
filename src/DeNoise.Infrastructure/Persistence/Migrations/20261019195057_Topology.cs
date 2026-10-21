using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeNoise.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Topology : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "policy_kind_chk",
                schema: "cfg",
                table: "policy");

            migrationBuilder.AddColumn<string>(
                name: "dependency_action",
                schema: "alert",
                table: "episode",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "dependency_released_at",
                schema: "alert",
                table: "episode",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "dependency_rule_id",
                schema: "alert",
                table: "episode",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "dependency_window_ends_at",
                schema: "alert",
                table: "episode",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "depends_on_episode_id",
                schema: "alert",
                table: "episode",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "policy_kind_chk",
                schema: "cfg",
                table: "policy",
                sql: "kind IN ('routing','escalation','lifecycle','grouping','topology')");

            migrationBuilder.CreateIndex(
                name: "episode_depends_on_idx",
                schema: "alert",
                table: "episode",
                column: "depends_on_episode_id",
                filter: "depends_on_episode_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "episode_scope_first_seen_idx",
                schema: "alert",
                table: "episode",
                columns: new[] { "access_scope", "first_seen" },
                filter: "handling_state <> 'closed'");

            migrationBuilder.AddForeignKey(
                name: "episode_depends_on_fk",
                schema: "alert",
                table: "episode",
                column: "depends_on_episode_id",
                principalSchema: "alert",
                principalTable: "episode",
                principalColumn: "episode_id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "episode_depends_on_fk",
                schema: "alert",
                table: "episode");

            migrationBuilder.DropCheckConstraint(
                name: "policy_kind_chk",
                schema: "cfg",
                table: "policy");

            migrationBuilder.DropIndex(
                name: "episode_depends_on_idx",
                schema: "alert",
                table: "episode");

            migrationBuilder.DropIndex(
                name: "episode_scope_first_seen_idx",
                schema: "alert",
                table: "episode");

            migrationBuilder.DropColumn(
                name: "dependency_action",
                schema: "alert",
                table: "episode");

            migrationBuilder.DropColumn(
                name: "dependency_released_at",
                schema: "alert",
                table: "episode");

            migrationBuilder.DropColumn(
                name: "dependency_rule_id",
                schema: "alert",
                table: "episode");

            migrationBuilder.DropColumn(
                name: "dependency_window_ends_at",
                schema: "alert",
                table: "episode");

            migrationBuilder.DropColumn(
                name: "depends_on_episode_id",
                schema: "alert",
                table: "episode");

            migrationBuilder.AddCheckConstraint(
                name: "policy_kind_chk",
                schema: "cfg",
                table: "policy",
                sql: "kind IN ('routing','escalation','lifecycle','grouping')");
        }
    }
}
