using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XpedeonAgentMissionControl.Migrations
{
    /// <inheritdoc />
    public partial class AddObservability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentSLAs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    TargetCompletionSeconds = table.Column<int>(type: "int", nullable: false),
                    MaxCompletionSeconds = table.Column<int>(type: "int", nullable: false),
                    TargetSuccessRatePercent = table.Column<double>(type: "float", nullable: false),
                    TargetAvailabilityPercent = table.Column<double>(type: "float", nullable: false),
                    MaxConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    MeasurementWindowHours = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSLAs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentSLAs_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SLAViolations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    TaskId = table.Column<int>(type: "int", nullable: true),
                    ViolationType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TargetValue = table.Column<double>(type: "float", nullable: false),
                    ActualValue = table.Column<double>(type: "float", nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsAcknowledged = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SLAViolations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SLAViolations_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SLAViolations_AgentTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "AgentTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AgentMetricsSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    TasksCompleted = table.Column<int>(type: "int", nullable: false),
                    TasksFailed = table.Column<int>(type: "int", nullable: false),
                    TasksRunning = table.Column<int>(type: "int", nullable: false),
                    SuccessRatePercent = table.Column<double>(type: "float", nullable: false),
                    AvgCompletionMs = table.Column<double>(type: "float", nullable: false),
                    P95CompletionMs = table.Column<double>(type: "float", nullable: false),
                    TotalTokensConsumed = table.Column<long>(type: "bigint", nullable: false),
                    TotalCostUSD = table.Column<decimal>(type: "decimal(18, 4)", nullable: false),
                    AvgCostPerTask = table.Column<decimal>(type: "decimal(18, 4)", nullable: false),
                    DailyBudgetUsagePercent = table.Column<int>(type: "int", nullable: false),
                    IsSLAMet = table.Column<bool>(type: "bit", nullable: false),
                    SnapshotAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentMetricsSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentMetricsSnapshots_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Indexes
            migrationBuilder.CreateIndex(
                name: "IX_AgentSLAs_AgentId",
                table: "AgentSLAs",
                column: "AgentId",
                isUnique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SLAViolations_AgentId",
                table: "SLAViolations",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_SLAViolations_OccurredAt",
                table: "SLAViolations",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_SLAViolations_ViolationType",
                table: "SLAViolations",
                column: "ViolationType");

            migrationBuilder.CreateIndex(
                name: "IX_SLAViolations_IsAcknowledged",
                table: "SLAViolations",
                column: "IsAcknowledged");

            migrationBuilder.CreateIndex(
                name: "IX_AgentMetricsSnapshots_AgentId",
                table: "AgentMetricsSnapshots",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentMetricsSnapshots_SnapshotAt",
                table: "AgentMetricsSnapshots",
                column: "SnapshotAt");

            migrationBuilder.CreateIndex(
                name: "IX_AgentMetricsSnapshots_IsSLAMet",
                table: "AgentMetricsSnapshots",
                column: "IsSLAMet");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AgentSLAs");
            migrationBuilder.DropTable(name: "SLAViolations");
            migrationBuilder.DropTable(name: "AgentMetricsSnapshots");
        }
    }
}
