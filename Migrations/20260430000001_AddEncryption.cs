using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XpedeonAgentMissionControl.Migrations
{
    /// <inheritdoc />
    public partial class AddEncryption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EncryptedConfigValues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    KeyName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EncryptedValue = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    KeyVersion = table.Column<int>(type: "int", nullable: false),
                    EncryptedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EncryptionKeyExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RequiresKeyRotation = table.Column<bool>(type: "bit", nullable: false),
                    RelatedEntityId = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EncryptedConfigValues", x => x.Id);
                });

            // Add encryption metadata columns to LLMProviders
            migrationBuilder.AddColumn<bool>(
                name: "IsApiKeyEncrypted",
                table: "LLMProviders",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsAuthTokenEncrypted",
                table: "LLMProviders",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "EncryptionUpdatedAt",
                table: "LLMProviders",
                type: "datetime2",
                nullable: true);

            // Add cost tracking columns to LLMProviders
            migrationBuilder.AddColumn<decimal>(
                name: "CostPer1kInputTokens",
                table: "LLMProviders",
                type: "decimal(18,10)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "CostPer1kOutputTokens",
                table: "LLMProviders",
                type: "decimal(18,10)",
                nullable: false,
                defaultValue: 0m);

            // Create index for faster lookups
            migrationBuilder.CreateIndex(
                name: "IX_EncryptedConfigValues_KeyName",
                table: "EncryptedConfigValues",
                column: "KeyName");

            migrationBuilder.CreateIndex(
                name: "IX_EncryptedConfigValues_RequiresKeyRotation",
                table: "EncryptedConfigValues",
                column: "RequiresKeyRotation");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EncryptedConfigValues");

            migrationBuilder.DropIndex(
                name: "IX_EncryptedConfigValues_KeyName",
                table: "EncryptedConfigValues");

            migrationBuilder.DropIndex(
                name: "IX_EncryptedConfigValues_RequiresKeyRotation",
                table: "EncryptedConfigValues");

            migrationBuilder.DropColumn(
                name: "IsApiKeyEncrypted",
                table: "LLMProviders");

            migrationBuilder.DropColumn(
                name: "IsAuthTokenEncrypted",
                table: "LLMProviders");

            migrationBuilder.DropColumn(
                name: "EncryptionUpdatedAt",
                table: "LLMProviders");

            migrationBuilder.DropColumn(
                name: "CostPer1kInputTokens",
                table: "LLMProviders");

            migrationBuilder.DropColumn(
                name: "CostPer1kOutputTokens",
                table: "LLMProviders");
        }
    }
}
