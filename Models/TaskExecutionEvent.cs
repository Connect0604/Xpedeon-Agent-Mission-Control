namespace XpedeonAgentMissionControl.Models;

public enum TaskExecutionEventType
{
    TaskCreated,
    TaskPendingApproval,
    TaskApproved,
    TaskCancelled,
    TaskCompleted,
    TaskFailed,
    PromptBuilt,
    SkillsApplied,
    MCPServersAttached,
    MCPToolsDiscovered,
    MCPToolRequested,
    MCPToolBlocked,
    MCPToolInvoked,
    MCPToolResult,
    SpawnStarted,
    SpawnDecomposed,
    SpawnChildCreated,
    SpawnChildCompleted,
    SpawnAggregationCompleted
}

public class TaskExecutionEvent
{
    public string Id { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public TaskExecutionEventType EventType { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? DetailsJson { get; set; }
    public string? SkillDefinitionId { get; set; }
    public string? SkillName { get; set; }
    public string? MCPServerId { get; set; }
    public string? MCPServerName { get; set; }
    public string? ToolName { get; set; }
    public string? RelatedTaskId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public AgentTask? Task { get; set; }
}
