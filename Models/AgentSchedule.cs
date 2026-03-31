namespace XpedeonAgentMissionControl.Models;

public class AgentSchedule
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string AgentId { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public string? DefaultInput { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public bool IsEnabled { get; set; } = true;
    public DateTime? LastRunAt { get; set; }
    public DateTime? NextRunAt { get; set; }
    public int TotalRuns { get; set; } = 0;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Agent? Agent { get; set; }
}
