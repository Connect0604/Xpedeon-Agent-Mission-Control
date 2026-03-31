namespace XpedeonAgentMissionControl.Models;

public enum LogLevel { Trace, Debug, Info, Success, Warning, Error, Critical }

public class LogEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? AgentId { get; set; }
    public string AgentName { get; set; } = "System";
    public LogLevel Level { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string? CorrelationId { get; set; }
    public string? TaskId { get; set; }
    public DateTime Timestamp { get; set; }

    // Navigation
    public Agent? Agent { get; set; }
}
