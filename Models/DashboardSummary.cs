namespace XpedeonAgentMissionControl.Models;

public class DashboardSummary
{
    public int TotalAgents { get; set; }
    public int ActiveAgents { get; set; }
    public int IdleAgents { get; set; }
    public int WarningAgents { get; set; }
    public int ErrorAgents { get; set; }
    public int OfflineAgents { get; set; }
    public int TotalTasksToday { get; set; }
    public int RunningTasks { get; set; }
    public int QueuedTasks { get; set; }
    public int CompletedTasksToday { get; set; }
    public int FailedTasksToday { get; set; }
    public double AverageSuccessRate { get; set; }
    public double TotalCpuUsage { get; set; }

    // Cost & token tracking
    public long TotalTokensToday { get; set; }
    public decimal TotalCostToday { get; set; }

    // Swarms
    public int TotalSwarms { get; set; }
    public int ActiveSwarms { get; set; }

    // Feedback
    public int PendingApprovals { get; set; }

    public double SuccessRatePct => TotalTasksToday > 0
        ? (double)CompletedTasksToday / TotalTasksToday * 100
        : 100;
}
