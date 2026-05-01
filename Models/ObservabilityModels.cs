namespace XpedeonAgentMissionControl.Models;

/// <summary>
/// SLA (Service Level Agreement) definition for an agent
/// </summary>
public class AgentSLA
{
    public int Id { get; set; }
    public int AgentId { get; set; }

    /// <summary>
    /// Target task completion time in seconds (P95)
    /// </summary>
    public int TargetCompletionSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum allowed task completion time in seconds
    /// </summary>
    public int MaxCompletionSeconds { get; set; } = 120;

    /// <summary>
    /// Target success rate percentage (0-100)
    /// </summary>
    public double TargetSuccessRatePercent { get; set; } = 95.0;

    /// <summary>
    /// Target availability percentage (0-100)
    /// </summary>
    public double TargetAvailabilityPercent { get; set; } = 99.0;

    /// <summary>
    /// Maximum number of consecutive failures before SLA breach
    /// </summary>
    public int MaxConsecutiveFailures { get; set; } = 3;

    /// <summary>
    /// Measurement window in hours (default: 24 hours)
    /// </summary>
    public int MeasurementWindowHours { get; set; } = 24;

    /// <summary>
    /// Whether SLA is actively enforced
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When SLA was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When SLA was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Agent? Agent { get; set; }
}

/// <summary>
/// SLA violation record
/// </summary>
public class SLAViolation
{
    public int Id { get; set; }
    public int AgentId { get; set; }
    public int? TaskId { get; set; }

    /// <summary>
    /// Type: "CompletionTime", "SuccessRate", "Availability", "ConsecutiveFailures"
    /// </summary>
    public string ViolationType { get; set; } = string.Empty;

    /// <summary>
    /// Target value that was not met
    /// </summary>
    public double TargetValue { get; set; }

    /// <summary>
    /// Actual value at time of violation
    /// </summary>
    public double ActualValue { get; set; }

    /// <summary>
    /// Severity: "Warning", "Breach"
    /// </summary>
    public string Severity { get; set; } = "Warning";

    /// <summary>
    /// Human-readable description of what was violated
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// When violation occurred
    /// </summary>
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether violation was acknowledged
    /// </summary>
    public bool IsAcknowledged { get; set; }

    // Navigation
    public Agent? Agent { get; set; }
    public AgentTask? Task { get; set; }
}

/// <summary>
/// Metrics snapshot for an agent at a point in time
/// </summary>
public class AgentMetricsSnapshot
{
    public int Id { get; set; }
    public int AgentId { get; set; }

    /// <summary>
    /// Tasks completed in measurement window
    /// </summary>
    public int TasksCompleted { get; set; }

    /// <summary>
    /// Tasks failed in measurement window
    /// </summary>
    public int TasksFailed { get; set; }

    /// <summary>
    /// Tasks currently running
    /// </summary>
    public int TasksRunning { get; set; }

    /// <summary>
    /// Success rate: 0-100
    /// </summary>
    public double SuccessRatePercent { get; set; }

    /// <summary>
    /// Average task completion time in ms
    /// </summary>
    public double AvgCompletionMs { get; set; }

    /// <summary>
    /// P95 task completion time in ms
    /// </summary>
    public double P95CompletionMs { get; set; }

    /// <summary>
    /// Total tokens consumed in window
    /// </summary>
    public long TotalTokensConsumed { get; set; }

    /// <summary>
    /// Total cost incurred in window (USD)
    /// </summary>
    public decimal TotalCostUSD { get; set; }

    /// <summary>
    /// Average cost per task (USD)
    /// </summary>
    public decimal AvgCostPerTask { get; set; }

    /// <summary>
    /// Current daily budget usage percent
    /// </summary>
    public int DailyBudgetUsagePercent { get; set; }

    /// <summary>
    /// Whether SLA is currently met
    /// </summary>
    public bool IsSLAMet { get; set; } = true;

    /// <summary>
    /// When snapshot was taken
    /// </summary>
    public DateTime SnapshotAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Agent? Agent { get; set; }
}

/// <summary>
/// Correlation ID for distributed tracing
/// </summary>
public class CorrelationContext
{
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString();
    public string? ParentSpanId { get; set; }
    public int? AgentId { get; set; }
    public int? TaskId { get; set; }
    public string? RequestPath { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
