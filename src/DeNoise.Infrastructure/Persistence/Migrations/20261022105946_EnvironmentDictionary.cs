using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeNoise.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnvironmentDictionary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "environment",
                schema: "cfg",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_built_in = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_environment", x => x.key);
                    table.CheckConstraint("environment_key_chk", "key ~ '^[a-z][a-z0-9_-]{0,31}$'");
                    table.CheckConstraint("environment_label_chk", "length(btrim(label)) between 1 and 64");
                });

            // ADR-17 seed: the classes spec §15.5 names, plus `ci` and `ephemeral`, which the pipeline reference mappings (M16) produce
            // and which their activation would otherwise refuse. `unknown` is built in and last.
            migrationBuilder.Sql("""
                INSERT INTO cfg.environment (key, label, sort_order, is_built_in, version, created_at, updated_at) VALUES
                  ('production',  'Production',  10,   false, 1, now(), now()),
                  ('staging',     'Staging',     20,   false, 1, now(), now()),
                  ('development', 'Development', 30,   false, 1, now(), now()),
                  ('ci',          'CI',          40,   false, 1, now(), now()),
                  ('ephemeral',   'Ephemeral',   50,   false, 1, now(), now()),
                  ('unknown',     'Unknown',     1000, true,  1, now(), now())
                ON CONFLICT (key) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "environment",
                schema: "cfg");
        }
    }
}
