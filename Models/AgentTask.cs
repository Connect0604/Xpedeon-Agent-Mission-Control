namespace XpedeonAgentMissionControl.Models;

public enum AgentTaskStatus { Queued, Running, Completed, Failed, Cancelled }

public enum TaskPriority { Low, Medium, High, Critical }

public class AgentTask
{
    public string Id { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TaskType { get; set; } = string.Empty;
    public AgentTaskStatus Status { get; set; }
    public TaskPriority Priority { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int Progress { get; set; }
    public long RecordsProcessed { get; set; }
    public long RecordsTotal { get; set; }
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }

    public TimeSpan? Duration => Status == AgentTaskStatus.Running && StartedAt.HasValue
        ? DateTime.UtcNow - StartedAt.Value
        : CompletedAt.HasValue && StartedAt.HasValue
            ? CompletedAt.Value - StartedAt.Value
            : null;
}
