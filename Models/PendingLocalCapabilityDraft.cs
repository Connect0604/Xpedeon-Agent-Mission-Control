namespace XpedeonAgentMissionControl.Models;

public sealed class PendingLocalCapabilityDraft
{
    public string TaskId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string DraftName { get; set; } = string.Empty;
    public string DraftDescription { get; set; } = string.Empty;
    public string DraftCategory { get; set; } = string.Empty;
    public string DraftExecutionType { get; set; } = string.Empty;
    public string DraftInputsJson { get; set; } = "{}";
    public string InvocationInputsJson { get; set; } = "{}";
    public string ScriptPath { get; set; } = string.Empty;
    public string Script { get; set; } = string.Empty;
    public string? AllowedRootsJson { get; set; }
    public string? ApprovalEvidence { get; set; }
}
