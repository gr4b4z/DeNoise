using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeNoise.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExternalIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "external_issuer",
                schema: "cfg",
                table: "user",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "local_only",
                schema: "cfg",
                table: "user",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "roles_projected_at",
                schema: "cfg",
                table: "user",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "user_external_identity_uq",
                schema: "cfg",
                table: "user",
                columns: new[] { "external_issuer", "external_id" },
                unique: true,
                filter: "external_issuer IS NOT NULL AND external_id IS NOT NULL");

            // M11b / ADR-15: the bootstrap administrator is the break-glass account. Never projected from Entra, never matched
            // by e-mail, and its password keeps working when Auth:Local:Enabled is false. New bootstraps set the flag on creation.
            migrationBuilder.Sql("""
                UPDATE cfg."user" SET local_only = true
                WHERE created_by = 'bootstrap' AND username = 'admin' AND auth_provider = 'local';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "user_external_identity_uq",
                schema: "cfg",
                table: "user");

            migrationBuilder.DropColumn(
                name: "external_issuer",
                schema: "cfg",
                table: "user");

            migrationBuilder.DropColumn(
                name: "local_only",
                schema: "cfg",
                table: "user");

            migrationBuilder.DropColumn(
                name: "roles_projected_at",
                schema: "cfg",
                table: "user");
        }
    }
}
