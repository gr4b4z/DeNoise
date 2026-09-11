using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlertHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IngestAndQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.EnsureSchema(
                name: "cfg");

            migrationBuilder.CreateTable(
                name: "entry",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_type = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    actor_display = table.Column<string>(type: "text", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    target_type = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<string>(type: "text", nullable: false),
                    access_scope = table.Column<string>(type: "text", nullable: true),
                    before = table.Column<string>(type: "jsonb", nullable: true),
                    after = table.Column<string>(type: "jsonb", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    request_ip = table.Column<IPAddress>(type: "inet", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entry", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "integration",
                schema: "cfg",
                columns: table => new
                {
                    integration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    access_scope = table.Column<string>(type: "text", nullable: false),
                    owner_team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ingest_key_id = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    ingest_token_hash = table.Column<string>(type: "text", nullable: false),
                    hmac_secret_enc = table.Column<string>(type: "text", nullable: true),
                    hmac_config = table.Column<string>(type: "jsonb", nullable: true),
                    ip_allow_list = table.Column<string[]>(type: "text[]", nullable: true),
                    capabilities = table.Column<string>(type: "jsonb", nullable: false),
                    coverage = table.Column<string>(type: "jsonb", nullable: false),
                    profile_defaults = table.Column<string>(type: "jsonb", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    shadow = table.Column<bool>(type: "boolean", nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deactivated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: true),
                    source_yaml = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration", x => new { x.integration_id, x.version });
                    table.CheckConstraint("integration_type_chk", "type IN ('azure_monitor','atlas','generic_webhook')");
                });

            migrationBuilder.CreateIndex(
                name: "audit_actor_idx",
                schema: "audit",
                table: "entry",
                columns: new[] { "actor_id", "at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "audit_at_idx",
                schema: "audit",
                table: "entry",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "audit_target_idx",
                schema: "audit",
                table: "entry",
                columns: new[] { "target_type", "target_id", "at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "integration_ingest_key_idx",
                schema: "cfg",
                table: "integration",
                column: "ingest_key_id",
                unique: true,
                filter: "deactivated_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "integration_one_current_version",
                schema: "cfg",
                table: "integration",
                column: "integration_id",
                unique: true,
                filter: "deactivated_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entry",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "integration",
                schema: "cfg");
        }
    }
}
