namespace XpedeonAgentMissionControl.Models;

public enum AgentTaskStatus { Queued, Running, Completed, Failed, Cancelled, PendingApproval }

public enum TaskPriority { Low, Medium, High, Critical }

public enum TriggerSource { Manual, Scheduled, Event, Swarm, Chained }

public enum ExternalRunStatus { None, Submitted, Running, Completed, Failed, Cancelled }

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
    public string? ProviderSnapshotId { get; set; }
    public string? ProviderSnapshotName { get; set; }
    public string? ProviderSnapshotModel { get; set; }
    public string? SkillSnapshotJson { get; set; }
    public string? MCPServerSnapshotJson { get; set; }
    public string? ApprovalEvidence { get; set; }
    public string? ApprovalComment { get; set; }
    public string? ReplayOfTaskId { get; set; }
    public ExecutionBackend ExecutionBackendSnapshot { get; set; } = ExecutionBackend.Local;

    // Execution trace
    public string? ExecutionTrace { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public decimal CostUSD { get; set; }
    public string? ModelUsed { get; set; }
    public long DurationMs { get; set; }
    public int ToolCallsUsed { get; set; }
    public string? ExternalRunId { get; set; }
    public string? ExternalBackend { get; set; }
    public ExternalRunStatus ExternalStatus { get; set; } = ExternalRunStatus.None;
    public DateTime? ExternalSubmittedAt { get; set; }
    public DateTime? ExternalLastSyncedAt { get; set; }
    public string? ExternalTrace { get; set; }
    public string? ExternalResultJson { get; set; }
    public string? ExternalError { get; set; }

    public string? LocalCapabilityId { get; set; }
    public string? LocalCapabilityDraftJson { get; set; }
    public string? LocalCapabilityExecutionJson { get; set; }
    public string? LocalActionPlanJson { get; set; }
    public string? LocalActionResultJson { get; set; }
    public string? TouchedPathsJson { get; set; }
    public bool RequiresElevatedApproval { get; set; }

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
    public string? WorkflowRunId { get; set; }
    public string? WorkflowStepId { get; set; }
    public string? WorkflowStepRunId { get; set; }

    // Dynamic Spawning
    public int SpawnDepth { get; set; } = 0;
    public string? SpawnParentTaskId { get; set; }
    public int SpawnChildCount { get; set; } = 0;

    // Navigation
    public Agent? Agent { get; set; }
    public List<TaskExecutionEvent> ExecutionEvents { get; set; } = new();

    public TimeSpan? Duration => Status == AgentTaskStatus.Running && StartedAt.HasValue
        ? DateTime.UtcNow - StartedAt.Value
        : CompletedAt.HasValue && StartedAt.HasValue
            ? CompletedAt.Value - StartedAt.Value
            : null;
}
