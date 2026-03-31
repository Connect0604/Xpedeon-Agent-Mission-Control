namespace XpedeonAgentMissionControl.Models;

public enum PromptChangeReason { Manual, AutoRefinement, FeedbackDriven, Rollback }

public class PromptHistory
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string AgentId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string? PreviousPrompt { get; set; }
    public string? Diff { get; set; }
    public PromptChangeReason ChangeReason { get; set; }
    public string? ChangeNote { get; set; }
    public double? SuccessRateAtChange { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Agent? Agent { get; set; }
}
