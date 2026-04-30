using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XpedeonAgentMissionControl.Migrations
{
    /// <inheritdoc />
    public partial class AddRateLimiting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RateLimitPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    CallsPerMinute = table.Column<int>(type: "int", nullable: true),
                    CallsPerHour = table.Column<int>(type: "int", nullable: true),
                    CallsPerDay = table.Column<int>(type: "int", nullable: true),
                    TokensPerHour = table.Column<int>(type: "int", nullable: true),
                    TokensPerDay = table.Column<int>(type: "int", nullable: true),
                    MaxConcurrentTasks = table.Column<int>(type: "int", nullable: false),
                    LimitExceededAction = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RateLimitPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RateLimitPolicies_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RateLimitEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    LimitType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CurrentValue = table.Column<int>(type: "int", nullable: false),
                    LimitValue = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BlockReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RequestPath = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClientIpAddress = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RateLimitEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RateLimitEvents_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RateLimitedTaskQueues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    TaskId = table.Column<int>(type: "int", nullable: false),
                    QueueReason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    QueuePosition = table.Column<int>(type: "int", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EstimatedExecuteAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExecutedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    WasSkipped = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RateLimitedTaskQueues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RateLimitedTaskQueues_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RateLimitedTaskQueues_AgentTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "AgentTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Create indexes for performance
            migrationBuilder.CreateIndex(
                name: "IX_RateLimitPolicies_AgentId",
                table: "RateLimitPolicies",
                column: "AgentId",
                isUnique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RateLimitEvents_AgentId",
                table: "RateLimitEvents",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_RateLimitEvents_OccurredAt",
                table: "RateLimitEvents",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_RateLimitEvents_Action",
                table: "RateLimitEvents",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_RateLimitEvents_LimitType",
                table: "RateLimitEvents",
                column: "LimitType");

            migrationBuilder.CreateIndex(
                name: "IX_RateLimitedTaskQueues_AgentId",
                table: "RateLimitedTaskQueues",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_RateLimitedTaskQueues_TaskId",
                table: "RateLimitedTaskQueues",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_RateLimitedTaskQueues_QueuedAt",
                table: "RateLimitedTaskQueues",
                column: "QueuedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RateLimitedTaskQueues_ExecutedAt",
                table: "RateLimitedTaskQueues",
                column: "ExecutedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RateLimitedTaskQueues_WasSkipped",
                table: "RateLimitedTaskQueues",
                column: "WasSkipped");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RateLimitPolicies");

            migrationBuilder.DropTable(
                name: "RateLimitEvents");

            migrationBuilder.DropTable(
                name: "RateLimitedTaskQueues");
        }
    }
}
