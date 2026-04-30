using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XpedeonAgentMissionControl.Migrations
{
    /// <inheritdoc />
    public partial class AddResilience : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RetryPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProviderId = table.Column<int>(type: "int", nullable: true),
                    MaxAttempts = table.Column<int>(type: "int", nullable: false),
                    BaseDelayMs = table.Column<int>(type: "int", nullable: false),
                    MaxDelayMs = table.Column<int>(type: "int", nullable: false),
                    BackoffMultiplier = table.Column<double>(type: "float", nullable: false),
                    JitterFactor = table.Column<double>(type: "float", nullable: false),
                    RetryOnStatusCodes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RetryOnTimeout = table.Column<bool>(type: "bit", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetryPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RetryPolicies_LLMProviders_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "LLMProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskExecutionAttempts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TaskId = table.Column<int>(type: "int", nullable: false),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    HttpStatusCode = table.Column<int>(type: "int", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    TokensUsed = table.Column<int>(type: "int", nullable: false),
                    CostUSD = table.Column<decimal>(type: "decimal(18, 4)", nullable: false),
                    NextRetryAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskExecutionAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskExecutionAttempts_AgentTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "AgentTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaskExecutionAttempts_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CircuitBreakerStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProviderId = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    SuccessCount = table.Column<int>(type: "int", nullable: false),
                    FailureCount = table.Column<int>(type: "int", nullable: false),
                    LastOpenedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextResetAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastTransitionAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FailureThreshold = table.Column<int>(type: "int", nullable: false),
                    OpenDurationSeconds = table.Column<int>(type: "int", nullable: false),
                    HalfOpenSuccessThreshold = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CircuitBreakerStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CircuitBreakerStates_LLMProviders_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "LLMProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskCheckpoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TaskId = table.Column<int>(type: "int", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StateJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Iteration = table.Column<int>(type: "int", nullable: false),
                    PartialOutput = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TokensUsed = table.Column<int>(type: "int", nullable: false),
                    CostUSD = table.Column<decimal>(type: "decimal(18, 4)", nullable: false),
                    IsRecoverable = table.Column<bool>(type: "bit", nullable: false),
                    SavedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskCheckpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskCheckpoints_AgentTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "AgentTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Performance indexes
            migrationBuilder.CreateIndex(
                name: "IX_RetryPolicies_ProviderId",
                table: "RetryPolicies",
                column: "ProviderId",
                isUnique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskExecutionAttempts_TaskId",
                table: "TaskExecutionAttempts",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskExecutionAttempts_AgentId",
                table: "TaskExecutionAttempts",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskExecutionAttempts_StartedAt",
                table: "TaskExecutionAttempts",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TaskExecutionAttempts_Outcome",
                table: "TaskExecutionAttempts",
                column: "Outcome");

            migrationBuilder.CreateIndex(
                name: "IX_TaskExecutionAttempts_NextRetryAt",
                table: "TaskExecutionAttempts",
                column: "NextRetryAt",
                filter: "[NextRetryAt] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CircuitBreakerStates_ProviderId",
                table: "CircuitBreakerStates",
                column: "ProviderId",
                isUnique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CircuitBreakerStates_State",
                table: "CircuitBreakerStates",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_TaskCheckpoints_TaskId",
                table: "TaskCheckpoints",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskCheckpoints_SavedAt",
                table: "TaskCheckpoints",
                column: "SavedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "RetryPolicies");
            migrationBuilder.DropTable(name: "TaskExecutionAttempts");
            migrationBuilder.DropTable(name: "CircuitBreakerStates");
            migrationBuilder.DropTable(name: "TaskCheckpoints");
        }
    }
}
