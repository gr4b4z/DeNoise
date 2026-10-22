using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeNoise.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class JobResult : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "result",
                schema: "ops",
                table: "job",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "result",
                schema: "ops",
                table: "job");
        }
    }
}
