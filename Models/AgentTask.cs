namespace XpedeonAgentMissionControl.Models;

public enum AgentTaskStatus { Queued, Running, Completed, Failed, Cancelled, PendingApproval }

public enum TaskPriority { Low, Medium, High, Critical }

public enum TriggerSource { Manual, Scheduled, Event, Swarm, Chained }

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
    public TriggerSource TriggerSource { get; set; } = TriggerSource.Manual;

    // Input / Output
    public string? Input { get; set; }
    public string? Output { get; set; }
    public string? SystemPromptSnapshot { get; set; }

    // Execution trace
    public string? ExecutionTrace { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public decimal CostUSD { get; set; }
    public string? ModelUsed { get; set; }

    // Progress
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int Progress { get; set; }
    public long RecordsProcessed { get; set; }
    public long RecordsTotal { get; set; }

    // Error handling
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }

    // Quality & feedback
    public double? ConfidenceScore { get; set; }
    public int? FeedbackRating { get; set; }
    public string? FeedbackNote { get; set; }

    // Chaining
    public string? ParentTaskId { get; set; }
    public string? NextAgentId { get; set; }

    // Navigation
    public Agent? Agent { get; set; }

    public TimeSpan? Duration => Status == AgentTaskStatus.Running && StartedAt.HasValue
        ? DateTime.UtcNow - StartedAt.Value
        : CompletedAt.HasValue && StartedAt.HasValue
            ? CompletedAt.Value - StartedAt.Value
            : null;
}
