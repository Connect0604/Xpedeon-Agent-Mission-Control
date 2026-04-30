namespace XpedeonAgentMissionControl.Models;

/// <summary>
/// Rate limiting policy for an agent
/// </summary>
public class RateLimitPolicy
{
    public int Id { get; set; }
    public int AgentId { get; set; }

    /// <summary>
    /// API calls per minute (null = unlimited)
    /// </summary>
    public int? CallsPerMinute { get; set; }

    /// <summary>
    /// API calls per hour (null = unlimited)
    /// </summary>
    public int? CallsPerHour { get; set; }

    /// <summary>
    /// API calls per day (null = unlimited)
    /// </summary>
    public int? CallsPerDay { get; set; }

    /// <summary>
    /// Tokens per hour (null = unlimited)
    /// </summary>
    public int? TokensPerHour { get; set; }

    /// <summary>
    /// Tokens per day (null = unlimited)
    /// </summary>
    public int? TokensPerDay { get; set; }

    /// <summary>
    /// Maximum concurrent tasks for this agent
    /// </summary>
    public int MaxConcurrentTasks { get; set; } = 5;

    /// <summary>
    /// Action to take when limit exceeded: "Block", "Queue", or "Degrade"
    /// </summary>
    public string LimitExceededAction { get; set; } = "Block";

    /// <summary>
    /// When policy was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When policy was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Agent? Agent { get; set; }
}

/// <summary>
/// Rate limit event (tracks limit violations and allows)
/// </summary>
public class RateLimitEvent
{
    public int Id { get; set; }
    public int AgentId { get; set; }

    /// <summary>
    /// Type of limit: "CallPerMinute", "CallPerHour", "CallPerDay", "TokenPerHour", "TokenPerDay", "ConcurrentTasks"
    /// </summary>
    public string LimitType { get; set; } = string.Empty;

    /// <summary>
    /// Current count/usage
    /// </summary>
    public int CurrentValue { get; set; }

    /// <summary>
    /// Configured limit
    /// </summary>
    public int LimitValue { get; set; }

    /// <summary>
    /// "Allowed" or "Blocked"
    /// </summary>
    public string Action { get; set; } = "Allowed";

    /// <summary>
    /// Reason if blocked
    /// </summary>
    public string? BlockReason { get; set; }

    /// <summary>
    /// When event occurred
    /// </summary>
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Request path that triggered limit
    /// </summary>
    public string? RequestPath { get; set; }

    /// <summary>
    /// Client IP address
    /// </summary>
    public string? ClientIpAddress { get; set; }

    // Navigation
    public Agent? Agent { get; set; }
}

/// <summary>
/// Rate limit queue entry (for tasks queued due to rate limits)
/// </summary>
public class RateLimitedTaskQueue
{
    public int Id { get; set; }
    public int AgentId { get; set; }
    public int TaskId { get; set; }

    /// <summary>
    /// Reason for queueing (which limit was exceeded)
    /// </summary>
    public string QueueReason { get; set; } = string.Empty;

    /// <summary>
    /// Position in queue (lower = higher priority)
    /// </summary>
    public int QueuePosition { get; set; }

    /// <summary>
    /// When task was queued
    /// </summary>
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Estimated time when task can execute
    /// </summary>
    public DateTime? EstimatedExecuteAt { get; set; }

    /// <summary>
    /// When task was actually dequeued and executed
    /// </summary>
    public DateTime? ExecutedAt { get; set; }

    /// <summary>
    /// Whether this task was skipped/abandoned
    /// </summary>
    public bool WasSkipped { get; set; }

    // Navigation
    public Agent? Agent { get; set; }
    public AgentTask? Task { get; set; }
}

/// <summary>
/// Rate limit statistics for monitoring
/// </summary>
public class RateLimitStatistics
{
    public int AgentId { get; set; }
    public int TotalCallsLastMinute { get; set; }
    public int TotalCallsLastHour { get; set; }
    public int TotalCallsLastDay { get; set; }
    public int TotalTokensLastHour { get; set; }
    public int TotalTokensLastDay { get; set; }
    public int CurrentConcurrentTasks { get; set; }
    public int QueuedTasksCount { get; set; }
    public int LimitViolationsLastHour { get; set; }
    public int LimitViolationsLastDay { get; set; }
    public DateTime AsOfDate { get; set; } = DateTime.UtcNow;
}
