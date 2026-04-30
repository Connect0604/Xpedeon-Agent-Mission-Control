using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XpedeonAgentMissionControl.Migrations
{
    /// <inheritdoc />
    public partial class AddHealthChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HealthCheckHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ComponentsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CheckedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResponseTimeMs = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthCheckHistories", x => x.Id);
                });

            // Create indexes for common queries
            migrationBuilder.CreateIndex(
                name: "IX_HealthCheckHistories_Status",
                table: "HealthCheckHistories",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_HealthCheckHistories_CheckedAt",
                table: "HealthCheckHistories",
                column: "CheckedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HealthCheckHistories");

            migrationBuilder.DropIndex(
                name: "IX_HealthCheckHistories_Status",
                table: "HealthCheckHistories");

            migrationBuilder.DropIndex(
                name: "IX_HealthCheckHistories_CheckedAt",
                table: "HealthCheckHistories");
        }
    }
}
