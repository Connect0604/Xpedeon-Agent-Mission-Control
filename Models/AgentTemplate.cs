namespace XpedeonAgentMissionControl.Models;

public enum TemplateCategory { Xpedeon, General, DataSync, Reporting, Integration, Notification, Processing }

public class AgentTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TemplateCategory Category { get; set; }
    public AgentType DefaultType { get; set; }
    public string SystemPrompt { get; set; } = string.Empty;
    public string? RecommendedModel { get; set; }
    public string SuggestedTools { get; set; } = "[]";    // JSON array
    public string SuggestedMCPServers { get; set; } = "[]"; // JSON array
    public bool IsXpedeonBuiltIn { get; set; } = false;
    public string? IconClass { get; set; }
    public string? Tags { get; set; }
    public int UsageCount { get; set; } = 0;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
