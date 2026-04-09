namespace XpedeonAgentMissionControl.Models;

public enum WorkflowStepType
{
    AgentTask,
    SwarmDispatch,
    ApprovalGate
}

public enum WorkflowRunStatus
{
    Queued,
    Running,
    PendingApproval,
    Completed,
    Failed,
    Cancelled
}

public enum WorkflowInputSource
{
    WorkflowInput,
    PreviousStepOutput,
    StaticText
}

public class AgentWorkflow
{
    public string Id { get; set; } = $"wf-{Guid.NewGuid():N}"[..12];
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public string Version { get; set; } = "1.0.0";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<AgentWorkflowStep> Steps { get; set; } = new();
    public List<WorkflowRun> Runs { get; set; } = new();
}

public class AgentWorkflowStep
{
    public string Id { get; set; } = $"wfs-{Guid.NewGuid():N}"[..12];
    public string WorkflowId { get; set; } = string.Empty;
    public AgentWorkflow? Workflow { get; set; }
    public int StepOrder { get; set; }
    public string Name { get; set; } = string.Empty;
    public WorkflowStepType StepType { get; set; }
    public string? AgentId { get; set; }
    public Agent? Agent { get; set; }
    public string? SwarmId { get; set; }
    public Swarm? Swarm { get; set; }
    public string? StaticInput { get; set; }
    public WorkflowInputSource InputSource { get; set; } = WorkflowInputSource.WorkflowInput;
    public string? PromptOverride { get; set; }
    public bool RequireApproval { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    public bool ContinueOnFailure { get; set; }

    public List<WorkflowStepRun> StepRuns { get; set; } = new();
}

public class WorkflowRun
{
    public string Id { get; set; } = $"wfr-{Guid.NewGuid():N}"[..12];
    public string WorkflowId { get; set; } = string.Empty;
    public AgentWorkflow? Workflow { get; set; }
    public string WorkflowNameSnapshot { get; set; } = string.Empty;
    public WorkflowRunStatus Status { get; set; } = WorkflowRunStatus.Queued;
    public string? Input { get; set; }
    public string? Output { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int CurrentStepOrder { get; set; }

    public List<WorkflowStepRun> StepRuns { get; set; } = new();
}

public class WorkflowStepRun
{
    public string Id { get; set; } = $"wsr-{Guid.NewGuid():N}"[..12];
    public string WorkflowRunId { get; set; } = string.Empty;
    public WorkflowRun? WorkflowRun { get; set; }
    public string WorkflowStepId { get; set; } = string.Empty;
    public AgentWorkflowStep? WorkflowStep { get; set; }
    public string StepNameSnapshot { get; set; } = string.Empty;
    public int StepOrder { get; set; }
    public WorkflowRunStatus Status { get; set; } = WorkflowRunStatus.Queued;
    public string? Input { get; set; }
    public string? Output { get; set; }
    public string? ErrorMessage { get; set; }
    public string? AgentTaskId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
