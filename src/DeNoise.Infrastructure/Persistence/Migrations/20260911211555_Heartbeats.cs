using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeNoise.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Heartbeats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "hb");

            migrationBuilder.CreateTable(
                name: "heartbeat",
                schema: "hb",
                columns: table => new
                {
                    heartbeat_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    access_scope = table.Column<string>(type: "text", nullable: false),
                    owning_team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    schedule_kind = table.Column<string>(type: "text", nullable: false),
                    interval = table.Column<TimeSpan>(type: "interval", nullable: true),
                    cron = table.Column<string>(type: "text", nullable: true),
                    schedule_tz = table.Column<string>(type: "text", nullable: true),
                    grace = table.Column<TimeSpan>(type: "interval", nullable: false),
                    severity_on_miss = table.Column<string>(type: "text", nullable: false),
                    routing_policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    binds_to_integration_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recovery_successes_required = table.Column<int>(type: "integer", nullable: false),
                    auto_pause_during_maintenance = table.Column<bool>(type: "boolean", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    expected_next = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_ping_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_ping_ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    last_run_duration = table.Column<TimeSpan>(type: "interval", nullable: true),
                    run_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consecutive_successes = table.Column<int>(type: "integer", nullable: false),
                    paused_by = table.Column<Guid>(type: "uuid", nullable: true),
                    paused_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pause_reason = table.Column<string>(type: "text", nullable: true),
                    paused_by_maintenance = table.Column<bool>(type: "boolean", nullable: false),
                    key_id = table.Column<string>(type: "text", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    token_rotated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    miss_episode_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_heartbeat", x => x.heartbeat_id);
                });

            migrationBuilder.CreateTable(
                name: "run",
                schema: "hb",
                columns: table => new
                {
                    heartbeat_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    exit_code = table.Column<int>(type: "integer", nullable: true),
                    body = table.Column<string>(type: "text", nullable: true),
                    source_ip = table.Column<IPAddress>(type: "inet", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run", x => new { x.heartbeat_id, x.seq });
                });

            migrationBuilder.CreateIndex(
                name: "hb_binding_idx",
                schema: "hb",
                table: "heartbeat",
                column: "binds_to_integration_id");

            migrationBuilder.CreateIndex(
                name: "hb_due_idx",
                schema: "hb",
                table: "heartbeat",
                column: "expected_next",
                filter: "state IN ('healthy','late')");

            migrationBuilder.CreateIndex(
                name: "hb_key_idx",
                schema: "hb",
                table: "heartbeat",
                column: "key_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "hb_team_idx",
                schema: "hb",
                table: "heartbeat",
                columns: new[] { "owning_team_id", "state" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "heartbeat",
                schema: "hb");

            migrationBuilder.DropTable(
                name: "run",
                schema: "hb");
        }
    }
}
