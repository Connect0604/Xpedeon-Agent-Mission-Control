using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XpedeonAgentMissionControl.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlertNotificationConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    EmailAddress = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SlackWebhookUrl = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SlackChannelName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PagerDutyKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NotifyAt50Percent = table.Column<bool>(type: "bit", nullable: false),
                    NotifyAt75Percent = table.Column<bool>(type: "bit", nullable: false),
                    NotifyAt90Percent = table.Column<bool>(type: "bit", nullable: false),
                    NotifyAt100Percent = table.Column<bool>(type: "bit", nullable: false),
                    MinutesBeforeDuplicateAlert = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertNotificationConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertNotificationConfigs_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AlertEscalationRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    ThresholdPercent = table.Column<int>(type: "int", nullable: false),
                    Period = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MessageTemplate = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertEscalationRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertEscalationRules_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AlertNotificationHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    CostAlertId = table.Column<int>(type: "int", nullable: false),
                    NotificationType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Recipient = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ThresholdPercent = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    WasSent = table.Column<bool>(type: "bit", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertNotificationHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertNotificationHistories_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AlertNotificationHistories_CostAlerts_CostAlertId",
                        column: x => x.CostAlertId,
                        principalTable: "CostAlerts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AlertAcknowledgments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CostAlertId = table.Column<int>(type: "int", nullable: false),
                    AcknowledgedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ActionTaken = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AcknowledgedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertAcknowledgments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertAcknowledgments_CostAlerts_CostAlertId",
                        column: x => x.CostAlertId,
                        principalTable: "CostAlerts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Create indexes for performance
            migrationBuilder.CreateIndex(
                name: "IX_AlertNotificationConfigs_AgentId",
                table: "AlertNotificationConfigs",
                column: "AgentId",
                isUnique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlertEscalationRules_AgentId",
                table: "AlertEscalationRules",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertEscalationRules_IsActive",
                table: "AlertEscalationRules",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_AlertNotificationHistories_AgentId",
                table: "AlertNotificationHistories",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertNotificationHistories_CostAlertId",
                table: "AlertNotificationHistories",
                column: "CostAlertId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertNotificationHistories_SentAt",
                table: "AlertNotificationHistories",
                column: "SentAt");

            migrationBuilder.CreateIndex(
                name: "IX_AlertNotificationHistories_WasSent",
                table: "AlertNotificationHistories",
                column: "WasSent");

            migrationBuilder.CreateIndex(
                name: "IX_AlertAcknowledgments_CostAlertId",
                table: "AlertAcknowledgments",
                column: "CostAlertId",
                isUnique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertNotificationConfigs");

            migrationBuilder.DropTable(
                name: "AlertEscalationRules");

            migrationBuilder.DropTable(
                name: "AlertNotificationHistories");

            migrationBuilder.DropTable(
                name: "AlertAcknowledgments");
        }
    }
}
