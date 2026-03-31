namespace XpedeonAgentMissionControl.Models;

public enum AgentStatus { Active, Idle, Warning, Error, Offline }

public enum AgentType { DataSync, Reporting, Integration, Notification, Processing }

public class Agent
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public AgentType Type { get; set; }
    public AgentStatus Status { get; set; }
    public string Description { get; set; } = string.Empty;
    public string CurrentTask { get; set; } = "Idle";
    public string Version { get; set; } = "1.0.0";
    public string HostMachine { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime LastSeen { get; set; }
    public int TasksCompleted { get; set; }
    public int TasksFailed { get; set; }
    public double CpuUsage { get; set; }
    public double MemoryUsageMB { get; set; }
    public double MemoryLimitMB { get; set; } = 512;
    public List<double> CpuHistory { get; set; } = new();

    public TimeSpan Uptime => DateTime.UtcNow - StartedAt;
    public double SuccessRate => (TasksCompleted + TasksFailed) > 0
        ? (double)TasksCompleted / (TasksCompleted + TasksFailed) * 100
        : 100;

    public string UptimeFormatted
    {
        get
        {
            var up = Uptime;
            if (up.TotalDays >= 1) return $"{(int)up.TotalDays}d {up.Hours}h";
            if (up.TotalHours >= 1) return $"{(int)up.TotalHours}h {up.Minutes}m";
            return $"{up.Minutes}m";
        }
    }
}
