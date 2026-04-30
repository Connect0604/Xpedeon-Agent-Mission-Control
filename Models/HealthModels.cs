namespace XpedeonAgentMissionControl.Models;

/// <summary>
/// Health status models for system monitoring
/// </summary>

public enum HealthStatus
{
    Healthy = 0,
    Degraded = 1,
    Unhealthy = 2,
    Unknown = 3
}

/// <summary>
/// Status of a single system component
/// </summary>
public class ComponentHealth
{
    public string Name { get; set; } = string.Empty;
    public HealthStatus Status { get; set; } = HealthStatus.Unknown;
    public string? Message { get; set; }
    public Dictionary<string, object> Details { get; set; } = new();
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    public long? ResponseTimeMs { get; set; }
}

/// <summary>
/// Overall system health report
/// </summary>
public class HealthReport
{
    public HealthStatus Status { get; set; } = HealthStatus.Unknown;
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    public List<ComponentHealth> Components { get; set; } = new();

    // Computed properties
    public int HealthyCount => Components.Count(c => c.Status == HealthStatus.Healthy);
    public int DegradedCount => Components.Count(c => c.Status == HealthStatus.Degraded);
    public int UnhealthyCount => Components.Count(c => c.Status == HealthStatus.Unhealthy);

    public string StatusString => Status.ToString().ToLower();
}

/// <summary>
/// Detailed health status stored in database for history
/// </summary>
public class HealthCheckHistory
{
    public int Id { get; set; }
    public HealthStatus Status { get; set; }
    public string ComponentsJson { get; set; } = string.Empty;
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    public long? ResponseTimeMs { get; set; }
}

/// <summary>
/// Health check configuration per component
/// </summary>
public class ComponentHealthConfig
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int TimeoutMs { get; set; } = 5000;
    public int? WarningThreshold { get; set; } // e.g., for queue depth
    public int? ErrorThreshold { get; set; }
}
