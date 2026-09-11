using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlertHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RoutingAndNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "delivery_attempt",
                schema: "ops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    outbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    http_status = table.Column<int>(type: "integer", nullable: true),
                    latency_ms = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    used_fallback = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    response_excerpt = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_attempt", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "policy",
                schema: "cfg",
                columns: table => new
                {
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    body = table.Column<string>(type: "jsonb", nullable: false),
                    source_yaml = table.Column<string>(type: "text", nullable: true),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deactivated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_policy", x => new { x.kind, x.policy_id, x.version });
                    table.CheckConstraint("policy_kind_chk", "kind IN ('routing','escalation','lifecycle','grouping')");
                });

            migrationBuilder.CreateTable(
                name: "scope",
                schema: "cfg",
                columns: table => new
                {
                    scope = table.Column<string>(type: "text", nullable: false),
                    product = table.Column<string>(type: "text", nullable: true),
                    entra_group_ids = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scope", x => x.scope);
                });

            migrationBuilder.CreateTable(
                name: "team",
                schema: "cfg",
                columns: table => new
                {
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    access_scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    fallback_team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    coverage_hours = table.Column<string>(type: "jsonb", nullable: true),
                    default_escalation_policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    entra_group_ids = table.Column<string[]>(type: "text[]", nullable: false),
                    is_triage = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team", x => x.team_id);
                });

            migrationBuilder.CreateTable(
                name: "destination",
                schema: "cfg",
                columns: table => new
                {
                    destination_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    channel_type = table.Column<string>(type: "text", nullable: false),
                    url_enc = table.Column<string>(type: "text", nullable: true),
                    method = table.Column<string>(type: "text", nullable: false, defaultValue: "POST"),
                    headers_enc = table.Column<string>(type: "text", nullable: true),
                    body_template_id = table.Column<Guid>(type: "uuid", nullable: true),
                    signing_secret_enc = table.Column<string>(type: "text", nullable: true),
                    timeout = table.Column<TimeSpan>(type: "interval", nullable: false),
                    event_types = table.Column<string[]>(type: "text[]", nullable: false),
                    retry_policy = table.Column<string>(type: "jsonb", nullable: true),
                    email_to = table.Column<string[]>(type: "text[]", nullable: true),
                    fallback_destination_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_failure_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_destination", x => x.destination_id);
                    table.CheckConstraint("destination_channel_chk", "channel_type IN ('webhook','smtp_email')");
                    table.CheckConstraint("destination_fallback_not_self_chk", "fallback_destination_id <> destination_id");
                    table.ForeignKey(
                        name: "destination_team_fk",
                        column: x => x.team_id,
                        principalSchema: "cfg",
                        principalTable: "team",
                        principalColumn: "team_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "delivery_attempt_at_idx",
                schema: "ops",
                table: "delivery_attempt",
                column: "attempted_at");

            migrationBuilder.CreateIndex(
                name: "delivery_attempt_outbox_idx",
                schema: "ops",
                table: "delivery_attempt",
                column: "outbox_id");

            migrationBuilder.CreateIndex(
                name: "destination_team_idx",
                schema: "cfg",
                table: "destination",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "policy_one_active_version",
                schema: "cfg",
                table: "policy",
                columns: new[] { "kind", "policy_id" },
                unique: true,
                filter: "activated_at IS NOT NULL AND deactivated_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "team_name_uq",
                schema: "cfg",
                table: "team",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "team_one_triage",
                schema: "cfg",
                table: "team",
                column: "is_triage",
                unique: true,
                filter: "is_triage");
            // Fallback FK deferred to commit so a bootstrap pair of destinations can reference each other in one transaction.
            migrationBuilder.Sql("""
                ALTER TABLE cfg.destination ADD CONSTRAINT destination_fallback_fk
                    FOREIGN KEY (fallback_destination_id) REFERENCES cfg.destination (destination_id) DEFERRABLE INITIALLY DEFERRED;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "delivery_attempt",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "destination",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "policy",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "scope",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "team",
                schema: "cfg");
        }
    }
}
