namespace XpedeonAgentMissionControl.Models;

public enum SkillCategory
{
    General,
    Database,
    Finance,
    Integration,
    Reporting,
    Compliance
}

public class SkillDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public SkillCategory Category { get; set; } = SkillCategory.General;
    public string Description { get; set; } = string.Empty;
    public string PromptSnippet { get; set; } = string.Empty;
    public string AllowedMcpToolNamesJson { get; set; } = "[]";
    public string? PreferredProviderId { get; set; }
    public string? PreferredModelName { get; set; }
    public int? MaxToolCalls { get; set; }
    public bool RequiresApproval { get; set; }
    public bool IsBuiltIn { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<AgentSkillAssignment> AgentAssignments { get; set; } = new();
}

public class AgentSkillAssignment
{
    public string AgentId { get; set; } = string.Empty;
    public string SkillDefinitionId { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    public Agent? Agent { get; set; }
    public SkillDefinition? SkillDefinition { get; set; }
}
