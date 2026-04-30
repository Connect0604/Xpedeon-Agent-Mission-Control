using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XpedeonAgentMissionControl.Migrations
{
    /// <inheritdoc />
    public partial class AddCostTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentBudgets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    DailyCostLimitUSD = table.Column<decimal>(type: "decimal(18, 4)", nullable: true),
                    MonthlyCostLimitUSD = table.Column<decimal>(type: "decimal(18, 4)", nullable: true),
                    AlertThresholdPercent = table.Column<int>(type: "int", nullable: false),
                    TodaySpentUSD = table.Column<decimal>(type: "decimal(18, 4)", nullable: false),
                    MonthSpentUSD = table.Column<decimal>(type: "decimal(18, 4)", nullable: false),
                    ProjectedMonthlyUSD = table.Column<decimal>(type: "decimal(18, 4)", nullable: false),
                    LastCheckedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DailyResetAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MonthlyResetAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentBudgets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentBudgets_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CostEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    TaskId = table.Column<int>(type: "int", nullable: false),
                    CostUSD = table.Column<decimal>(type: "decimal(18, 4)", nullable: false),
                    TokensUsed = table.Column<int>(type: "int", nullable: false),
                    InputTokens = table.Column<int>(type: "int", nullable: true),
                    OutputTokens = table.Column<int>(type: "int", nullable: true),
                    ProviderName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModelName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CostEvents_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CostEvents_AgentTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "AgentTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CostAlerts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    ThresholdPercent = table.Column<int>(type: "int", nullable: false),
                    Period = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CurrentSpentUSD = table.Column<decimal>(type: "decimal(18, 4)", nullable: false),
                    LimitUSD = table.Column<decimal>(type: "decimal(18, 4)", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsNotified = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostAlerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CostAlerts_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BudgetOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    OverrideAmountUSD = table.Column<decimal>(type: "decimal(18, 4)", nullable: false),
                    Period = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ApprovedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TicketRef = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BudgetOverrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BudgetOverrides_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Create indexes for performance
            migrationBuilder.CreateIndex(
                name: "IX_AgentBudgets_AgentId",
                table: "AgentBudgets",
                column: "AgentId",
                isUnique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CostEvents_AgentId",
                table: "CostEvents",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_CostEvents_TaskId",
                table: "CostEvents",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_CostEvents_OccurredAt",
                table: "CostEvents",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_CostAlerts_AgentId",
                table: "CostAlerts",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_CostAlerts_CreatedAt",
                table: "CostAlerts",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CostAlerts_IsNotified",
                table: "CostAlerts",
                column: "IsNotified");

            migrationBuilder.CreateIndex(
                name: "IX_BudgetOverrides_AgentId",
                table: "BudgetOverrides",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_BudgetOverrides_ExpiresAt",
                table: "BudgetOverrides",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentBudgets");

            migrationBuilder.DropTable(
                name: "CostEvents");

            migrationBuilder.DropTable(
                name: "CostAlerts");

            migrationBuilder.DropTable(
                name: "BudgetOverrides");
        }
    }
}
