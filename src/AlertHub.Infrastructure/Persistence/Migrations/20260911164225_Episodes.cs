using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlertHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Episodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "applied_event",
                schema: "alert",
                columns: table => new
                {
                    integration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_applied_event", x => new { x.integration_id, x.delivery_key });
                });

            migrationBuilder.CreateTable(
                name: "delivery_duplicate",
                schema: "alert",
                columns: table => new
                {
                    integration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false),
                    first_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_duplicate", x => new { x.integration_id, x.delivery_key });
                });

            migrationBuilder.CreateTable(
                name: "identity",
                schema: "alert",
                columns: table => new
                {
                    fingerprint = table.Column<byte[]>(type: "bytea", nullable: false),
                    integration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    access_scope = table.Column<string>(type: "text", nullable: false),
                    identity_version = table.Column<int>(type: "integer", nullable: false),
                    components = table.Column<string>(type: "jsonb", nullable: false),
                    first_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    episode_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity", x => x.fingerprint);
                });

            migrationBuilder.CreateTable(
                name: "mapping",
                schema: "cfg",
                columns: table => new
                {
                    mapping_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    integration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    evaluation_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 100),
                    name = table.Column<string>(type: "text", nullable: true),
                    body = table.Column<string>(type: "jsonb", nullable: false),
                    source_yaml = table.Column<string>(type: "text", nullable: true),
                    identity_version = table.Column<int>(type: "integer", nullable: false),
                    samples = table.Column<string>(type: "jsonb", nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deactivated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mapping", x => new { x.mapping_id, x.version });
                });

            migrationBuilder.CreateTable(
                name: "mapping_failure",
                schema: "alert",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    integration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    raw_received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    mapping_version = table.Column<int>(type: "integer", nullable: true),
                    error = table.Column<string>(type: "text", nullable: false),
                    field = table.Column<string>(type: "text", nullable: true),
                    quarantined = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mapping_failure", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "normalised_event",
                schema: "alert",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    integration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    raw_received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    mapping_version = table.Column<int>(type: "integer", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    source_alert_id = table.Column<string>(type: "text", nullable: true),
                    source_event_id = table.Column<string>(type: "text", nullable: true),
                    source_version = table.Column<string>(type: "text", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    source_severity = table.Column<string>(type: "text", nullable: true),
                    resource_id = table.Column<string>(type: "text", nullable: true),
                    resource_name = table.Column<string>(type: "text", nullable: true),
                    rule_id = table.Column<string>(type: "text", nullable: true),
                    rule_name = table.Column<string>(type: "text", nullable: true),
                    environment = table.Column<string>(type: "text", nullable: true),
                    service = table.Column<string>(type: "text", nullable: true),
                    summary = table.Column<string>(type: "text", nullable: true),
                    source_url = table.Column<string>(type: "text", nullable: true),
                    runbook_url = table.Column<string>(type: "text", nullable: true),
                    dimensions = table.Column<string>(type: "jsonb", nullable: true),
                    labels = table.Column<string>(type: "jsonb", nullable: true),
                    fingerprint = table.Column<byte[]>(type: "bytea", nullable: true),
                    identity_components = table.Column<string>(type: "jsonb", nullable: true),
                    delivery_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    identity_confidence = table.Column<string>(type: "text", nullable: false),
                    lifecycle_profile_hint = table.Column<string>(type: "text", nullable: true),
                    episode_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_normalised_event", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "source_instance_state",
                schema: "alert",
                columns: table => new
                {
                    integration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_alert_id = table.Column<string>(type: "text", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_instance_state", x => new { x.integration_id, x.source_alert_id });
                });

            migrationBuilder.CreateTable(
                name: "episode",
                schema: "alert",
                columns: table => new
                {
                    episode_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fingerprint = table.Column<byte[]>(type: "bytea", nullable: false),
                    integration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    access_scope = table.Column<string>(type: "text", nullable: false),
                    previous_episode_id = table.Column<Guid>(type: "uuid", nullable: true),
                    condition_state = table.Column<string>(type: "text", nullable: false),
                    handling_state = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    is_actionable = table.Column<bool>(type: "boolean", nullable: false),
                    first_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_applied_version = table.Column<string>(type: "text", nullable: true),
                    occurrence_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    owning_team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assignee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    routing_rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    routing_correction_required = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ack_deadline_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acknowledged_by = table.Column<Guid>(type: "uuid", nullable: true),
                    follow_up_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    auto_resolve_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lifecycle_policy_version = table.Column<int>(type: "integer", nullable: true),
                    lifecycle_profile = table.Column<string>(type: "text", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closure_reason = table.Column<string>(type: "text", nullable: true),
                    resolution_evidence = table.Column<string>(type: "text", nullable: true),
                    closure_note = table.Column<string>(type: "text", nullable: true),
                    restored_from_reason = table.Column<string>(type: "text", nullable: true),
                    suppressed_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    suppression_source = table.Column<string>(type: "text", nullable: true),
                    group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    summary = table.Column<string>(type: "text", nullable: true),
                    resource_name = table.Column<string>(type: "text", nullable: true),
                    rule_name = table.Column<string>(type: "text", nullable: true),
                    service = table.Column<string>(type: "text", nullable: true),
                    environment = table.Column<string>(type: "text", nullable: true),
                    source_url = table.Column<string>(type: "text", nullable: true),
                    runbook_url = table.Column<string>(type: "text", nullable: true),
                    source_alert_id = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_episode", x => x.episode_id);
                    table.ForeignKey(
                        name: "episode_identity_fk",
                        column: x => x.fingerprint,
                        principalSchema: "alert",
                        principalTable: "identity",
                        principalColumn: "fingerprint",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "episode_event",
                schema: "alert",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    episode_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    detail = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_episode_event", x => x.id);
                    table.ForeignKey(
                        name: "episode_event_episode_fk",
                        column: x => x.episode_id,
                        principalSchema: "alert",
                        principalTable: "episode",
                        principalColumn: "episode_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "applied_event_event_idx",
                schema: "alert",
                table: "applied_event",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "episode_closed_idx",
                schema: "alert",
                table: "episode",
                column: "closed_at",
                filter: "handling_state = 'closed'");

            migrationBuilder.CreateIndex(
                name: "episode_integration_idx",
                schema: "alert",
                table: "episode",
                columns: new[] { "integration_id", "handling_state" });

            migrationBuilder.CreateIndex(
                name: "episode_one_open_per_identity",
                schema: "alert",
                table: "episode",
                column: "fingerprint",
                unique: true,
                filter: "handling_state <> 'closed'");

            migrationBuilder.CreateIndex(
                name: "episode_queue_idx",
                schema: "alert",
                table: "episode",
                columns: new[] { "access_scope", "handling_state", "severity", "last_seen" },
                descending: new[] { false, false, false, true },
                filter: "handling_state <> 'closed'");

            migrationBuilder.CreateIndex(
                name: "episode_team_idx",
                schema: "alert",
                table: "episode",
                columns: new[] { "owning_team_id", "handling_state", "ack_deadline_at" });

            migrationBuilder.CreateIndex(
                name: "episode_event_episode_idx",
                schema: "alert",
                table: "episode_event",
                columns: new[] { "episode_id", "at" });

            migrationBuilder.CreateIndex(
                name: "mapping_integration_idx",
                schema: "cfg",
                table: "mapping",
                columns: new[] { "integration_id", "evaluation_order" });

            migrationBuilder.CreateIndex(
                name: "mapping_one_active_version",
                schema: "cfg",
                table: "mapping",
                column: "mapping_id",
                unique: true,
                filter: "activated_at IS NOT NULL AND deactivated_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "mapping_failure_integration_idx",
                schema: "alert",
                table: "mapping_failure",
                columns: new[] { "integration_id", "raw_received_at" },
                filter: "quarantined");

            migrationBuilder.CreateIndex(
                name: "normalised_event_episode_idx",
                schema: "alert",
                table: "normalised_event",
                column: "episode_id");

            migrationBuilder.CreateIndex(
                name: "normalised_event_integration_idx",
                schema: "alert",
                table: "normalised_event",
                columns: new[] { "integration_id", "received_at" },
                descending: new[] { false, true });
            // Full-text search over summary/resource/rule/service (05 §2, B5): generated column + GIN index, outside the EF model.
            migrationBuilder.Sql("""
                ALTER TABLE alert.episode ADD COLUMN search_tsv tsvector GENERATED ALWAYS AS (
                    to_tsvector('simple', coalesce(summary,'') || ' ' || coalesce(resource_name,'') || ' ' || coalesce(rule_name,'') || ' ' || coalesce(service,''))) STORED;
                CREATE INDEX episode_search_idx ON alert.episode USING gin (search_tsv);
                ALTER TABLE alert.episode SET (autovacuum_vacuum_scale_factor = 0.02, autovacuum_analyze_scale_factor = 0.02);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "applied_event",
                schema: "alert");

            migrationBuilder.DropTable(
                name: "delivery_duplicate",
                schema: "alert");

            migrationBuilder.DropTable(
                name: "episode_event",
                schema: "alert");

            migrationBuilder.DropTable(
                name: "mapping",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "mapping_failure",
                schema: "alert");

            migrationBuilder.DropTable(
                name: "normalised_event",
                schema: "alert");

            migrationBuilder.DropTable(
                name: "source_instance_state",
                schema: "alert");

            migrationBuilder.DropTable(
                name: "episode",
                schema: "alert");

            migrationBuilder.DropTable(
                name: "identity",
                schema: "alert");
        }
    }
}
