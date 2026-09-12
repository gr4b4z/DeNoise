using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeNoise.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ops");

            migrationBuilder.EnsureSchema(
                name: "alert");

            migrationBuilder.CreateTable(
                name: "hub_component_heartbeat",
                schema: "ops",
                columns: table => new
                {
                    component = table.Column<string>(type: "text", nullable: false),
                    instance = table.Column<string>(type: "text", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hub_component_heartbeat", x => x.component);
                });

            migrationBuilder.CreateTable(
                name: "job",
                schema: "ops",
                columns: table => new
                {
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    not_before = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    priority = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)100),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    episode_id = table.Column<Guid>(type: "uuid", nullable: true),
                    integration_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expected_version = table.Column<int>(type: "integer", nullable: true),
                    expected_last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    max_attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 10),
                    reserved_by = table.Column<string>(type: "text", nullable: true),
                    reserved_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job", x => x.job_id);
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "ops",
                columns: table => new
                {
                    outbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    episode_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "text", nullable: false),
                    destination_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version = table.Column<int>(type: "integer", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    not_before = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    reserved_by = table.Column<string>(type: "text", nullable: true),
                    reserved_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox", x => x.outbox_id);
                });

            // Schemas that later migrations populate (05 §1). Created here so grants can be applied once.
            migrationBuilder.EnsureSchema(name: "audit");
            migrationBuilder.EnsureSchema(name: "hb");
            migrationBuilder.EnsureSchema(name: "cfg");

            // alert.raw_event is range-partitioned by received_at from the very first migration (AGENTS.md §7);
            // EF Core cannot express PARTITION BY, so the table is created with SQL and mapped read/write only.
            // Daily partitions are created by RawEventPartitions (migrator after migrate; scheduler daily).
            migrationBuilder.Sql("""
                CREATE TABLE alert.raw_event (
                  event_id        uuid        NOT NULL,
                  integration_id  uuid        NOT NULL,
                  received_at     timestamptz NOT NULL,
                  content_type    text,
                  body            bytea       NOT NULL,
                  headers         jsonb,
                  source_ip       inet,
                  size_bytes      int         NOT NULL,
                  CONSTRAINT "PK_raw_event" PRIMARY KEY (received_at, event_id)
                ) PARTITION BY RANGE (received_at);
                CREATE INDEX raw_event_integration_idx ON alert.raw_event (integration_id, received_at DESC);
                """);

            // Hot queue tables: fillfactor 70 and aggressive autovacuum (ADR-2, 05 §7).
            migrationBuilder.Sql("""
                ALTER TABLE ops.job SET (fillfactor = 70, autovacuum_vacuum_scale_factor = 0.02, autovacuum_analyze_scale_factor = 0.02);
                ALTER TABLE ops.outbox SET (fillfactor = 70, autovacuum_vacuum_scale_factor = 0.02, autovacuum_analyze_scale_factor = 0.02);
                """);

            migrationBuilder.CreateIndex(
                name: "job_claim_idx",
                schema: "ops",
                table: "job",
                columns: new[] { "kind", "not_before" },
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "job_episode_idx",
                schema: "ops",
                table: "job",
                columns: new[] { "episode_id", "kind" },
                filter: "status IN ('pending','suspended')");

            migrationBuilder.CreateIndex(
                name: "job_one_timer_per_episode",
                schema: "ops",
                table: "job",
                columns: new[] { "episode_id", "kind" },
                unique: true,
                filter: "status IN ('pending','reserved','suspended') AND episode_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "outbox_claim_idx",
                schema: "ops",
                table: "outbox",
                column: "not_before",
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "outbox_episode_idx",
                schema: "ops",
                table: "outbox",
                columns: new[] { "episode_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "hub_component_heartbeat",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "job",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "raw_event",
                schema: "alert");
        }
    }
}
