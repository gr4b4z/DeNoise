using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeNoise.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PipelineSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "integration_type_chk",
                schema: "cfg",
                table: "integration");

            migrationBuilder.AddCheckConstraint(
                name: "integration_type_chk",
                schema: "cfg",
                table: "integration",
                sql: "type IN ('azure_monitor','atlas','generic_webhook','azure_devops','github')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "integration_type_chk",
                schema: "cfg",
                table: "integration");

            migrationBuilder.AddCheckConstraint(
                name: "integration_type_chk",
                schema: "cfg",
                table: "integration",
                sql: "type IN ('azure_monitor','atlas','generic_webhook')");
        }
    }
}
