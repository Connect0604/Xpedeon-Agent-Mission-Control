namespace XpedeonAgentMissionControl.Models;

public class WorkflowTaskContext
{
    public string WorkflowRunId { get; set; } = string.Empty;
    public string WorkflowStepId { get; set; } = string.Empty;
    public string WorkflowStepRunId { get; set; } = string.Empty;
}
