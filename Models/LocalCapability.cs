namespace XpedeonAgentMissionControl.Models;

public class LocalCapability
{
    public string Id { get; set; } = $"cap-{Guid.NewGuid():N}";
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public LocalCapabilityCategory Category { get; set; }
    public LocalCapabilityExecutionType ExecutionType { get; set; }
    public string? HandlerKey { get; set; }
    public string? ScriptPath { get; set; }
    public string? ScriptContent { get; set; }
    public string InputSchemaJson { get; set; } = "{}";
    public string OutputSchemaJson { get; set; } = "{}";
    public string? AllowedRootsJson { get; set; }
    public bool RequiresApproval { get; set; }
    public bool IsGenerated { get; set; }
    public bool IsActive { get; set; } = true;
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
}
