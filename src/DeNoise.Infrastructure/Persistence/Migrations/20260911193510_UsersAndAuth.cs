using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeNoise.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UsersAndAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "coverage_state",
                schema: "ops",
                columns: table => new
                {
                    integration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_signal_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consecutive_successes = table.Column<int>(type: "integer", nullable: false),
                    last_processed_alert_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    coverage_episode_id = table.Column<Guid>(type: "uuid", nullable: true),
                    detail = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coverage_state", x => x.integration_id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_key",
                schema: "cfg",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_fingerprint = table.Column<string>(type: "text", nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: false),
                    response_body = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_key", x => new { x.user_id, x.key });
                });

            migrationBuilder.CreateTable(
                name: "login_attempt",
                schema: "cfg",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    username = table.Column<string>(type: "text", nullable: false),
                    ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    success = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_login_attempt", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user",
                schema: "cfg",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    auth_provider = table.Column<string>(type: "text", nullable: false, defaultValue: "local"),
                    external_id = table.Column<string>(type: "text", nullable: true),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    password_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    must_change_password = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    totp_secret_enc = table.Column<string>(type: "text", nullable: true),
                    roles = table.Column<string[]>(type: "text[]", nullable: false),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user", x => x.user_id);
                    table.CheckConstraint("user_provider_chk", "auth_provider IN ('local','oidc')");
                });

            migrationBuilder.CreateTable(
                name: "personal_access_token",
                schema: "cfg",
                columns: table => new
                {
                    token_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    key_id = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_personal_access_token", x => x.token_id);
                    table.ForeignKey(
                        name: "pat_user_fk",
                        column: x => x.user_id,
                        principalSchema: "cfg",
                        principalTable: "user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "saved_filter",
                schema: "cfg",
                columns: table => new
                {
                    filter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    query = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saved_filter", x => x.filter_id);
                    table.ForeignKey(
                        name: "saved_filter_user_fk",
                        column: x => x.user_id,
                        principalSchema: "cfg",
                        principalTable: "user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "session",
                schema: "cfg",
                columns: table => new
                {
                    session_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    absolute_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    user_agent = table.Column<string>(type: "text", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session", x => x.session_id);
                    table.ForeignKey(
                        name: "session_user_fk",
                        column: x => x.user_id,
                        principalSchema: "cfg",
                        principalTable: "user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_member",
                schema: "cfg",
                columns: table => new
                {
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false, defaultValue: "member")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_member", x => new { x.team_id, x.user_id });
                    table.ForeignKey(
                        name: "team_member_team_fk",
                        column: x => x.team_id,
                        principalSchema: "cfg",
                        principalTable: "team",
                        principalColumn: "team_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "team_member_user_fk",
                        column: x => x.user_id,
                        principalSchema: "cfg",
                        principalTable: "user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idempotency_expires_idx",
                schema: "cfg",
                table: "idempotency_key",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "login_attempt_ip_idx",
                schema: "cfg",
                table: "login_attempt",
                columns: new[] { "ip", "at" });

            migrationBuilder.CreateIndex(
                name: "login_attempt_user_idx",
                schema: "cfg",
                table: "login_attempt",
                columns: new[] { "username", "at" });

            migrationBuilder.CreateIndex(
                name: "pat_key_uq",
                schema: "cfg",
                table: "personal_access_token",
                column: "key_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "pat_user_idx",
                schema: "cfg",
                table: "personal_access_token",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "saved_filter_user_name_uq",
                schema: "cfg",
                table: "saved_filter",
                columns: new[] { "user_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "session_expires_idx",
                schema: "cfg",
                table: "session",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "session_user_idx",
                schema: "cfg",
                table: "session",
                columns: new[] { "user_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "team_member_user_idx",
                schema: "cfg",
                table: "team_member",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "user_email_uq",
                schema: "cfg",
                table: "user",
                column: "email",
                unique: true,
                filter: "email IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "user_username_uq",
                schema: "cfg",
                table: "user",
                column: "username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "coverage_state",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "idempotency_key",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "login_attempt",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "personal_access_token",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "saved_filter",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "session",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "team_member",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "user",
                schema: "cfg");
        }
    }
}
