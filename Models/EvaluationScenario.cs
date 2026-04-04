namespace XpedeonAgentMissionControl.Models;

public class EvaluationScenario
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? BaseAgentId { get; set; }
    public string TaskName { get; set; } = string.Empty;
    public string Input { get; set; } = string.Empty;
    public string? PromptOverride { get; set; }
    public string SelectedProviderIdsJson { get; set; } = "[]";
    public string SelectedSkillIdsJson { get; set; } = "[]";
    public bool IsPinned { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastRunAt { get; set; }

    public Agent? BaseAgent { get; set; }
    public List<EvaluationRun> Runs { get; set; } = new();
}

public enum EvaluationRunStatus
{
    Completed,
    Failed
}

public class EvaluationRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string EvaluationScenarioId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string SkillNamesJson { get; set; } = "[]";
    public string Output { get; set; } = string.Empty;
    public string ExecutionTrace { get; set; } = string.Empty;
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public decimal CostUSD { get; set; }
    public int ToolCallsUsed { get; set; }
    public long DurationMs { get; set; }
    public EvaluationRunStatus Status { get; set; } = EvaluationRunStatus.Completed;
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public EvaluationScenario? Scenario { get; set; }
}
