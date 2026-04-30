namespace XpedeonAgentMissionControl.Models;

/// <summary>
/// Retry policy configuration per LLM provider
/// </summary>
public class RetryPolicy
{
    public int Id { get; set; }
    public int? ProviderId { get; set; }

    /// <summary>
    /// Maximum retry attempts (0 = no retry)
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// Base delay in milliseconds for exponential backoff
    /// </summary>
    public int BaseDelayMs { get; set; } = 1000;

    /// <summary>
    /// Maximum delay cap in milliseconds
    /// </summary>
    public int MaxDelayMs { get; set; } = 30000;

    /// <summary>
    /// Exponential backoff multiplier
    /// </summary>
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Jitter factor (0-1) to avoid thundering herd
    /// </summary>
    public double JitterFactor { get; set; } = 0.1;

    /// <summary>
    /// HTTP status codes to retry (e.g., "429,500,502,503,504")
    /// </summary>
    public string RetryOnStatusCodes { get; set; } = "429,500,502,503,504";

    /// <summary>
    /// Whether to retry on timeout
    /// </summary>
    public bool RetryOnTimeout { get; set; } = true;

    /// <summary>
    /// Request timeout in seconds
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// When policy was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public LLMProvider? Provider { get; set; }

    public IReadOnlyList<int> RetryableStatusCodes =>
        RetryOnStatusCodes?.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s.Trim(), out var n) ? n : 0)
            .Where(n => n > 0)
            .ToList() ?? new List<int>();
}

/// <summary>
/// Task execution attempt (tracks each retry)
/// </summary>
public class TaskExecutionAttempt
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public int AgentId { get; set; }

    /// <summary>
    /// Attempt number (1 = first try, 2 = first retry, etc.)
    /// </summary>
    public int AttemptNumber { get; set; }

    /// <summary>
    /// When attempt started
    /// </summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When attempt completed (null = in progress)
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Outcome: "Success", "Failed", "Timeout", "Cancelled"
    /// </summary>
    public string Outcome { get; set; } = "InProgress";

    /// <summary>
    /// Error message if failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// HTTP status code if API call failed
    /// </summary>
    public int? HttpStatusCode { get; set; }

    /// <summary>
    /// Duration in milliseconds
    /// </summary>
    public long? DurationMs { get; set; }

    /// <summary>
    /// Tokens used in this attempt (0 if failed early)
    /// </summary>
    public int TokensUsed { get; set; }

    /// <summary>
    /// Cost incurred (0 if failed before billing)
    /// </summary>
    public decimal CostUSD { get; set; }

    /// <summary>
    /// When next retry is scheduled (null = no retry planned)
    /// </summary>
    public DateTime? NextRetryAt { get; set; }

    // Navigation
    public AgentTask? Task { get; set; }
    public Agent? Agent { get; set; }
}

/// <summary>
/// Circuit breaker state for an LLM provider
/// </summary>
public class CircuitBreakerState
{
    public int Id { get; set; }
    public int ProviderId { get; set; }

    /// <summary>
    /// Circuit state: "Closed" (normal), "Open" (blocking), "HalfOpen" (testing)
    /// </summary>
    public string State { get; set; } = "Closed";

    /// <summary>
    /// Consecutive failure count
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// Total successes since last reset
    /// </summary>
    public int SuccessCount { get; set; }

    /// <summary>
    /// Total failures since last reset
    /// </summary>
    public int FailureCount { get; set; }

    /// <summary>
    /// When circuit was last opened
    /// </summary>
    public DateTime? LastOpenedAt { get; set; }

    /// <summary>
    /// When circuit should auto-reset to HalfOpen
    /// </summary>
    public DateTime? NextResetAt { get; set; }

    /// <summary>
    /// When circuit last transitioned states
    /// </summary>
    public DateTime LastTransitionAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Number of consecutive failures to open the circuit
    /// </summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>
    /// Seconds circuit remains Open before transitioning to HalfOpen
    /// </summary>
    public int OpenDurationSeconds { get; set; } = 60;

    /// <summary>
    /// Number of success probes required to close from HalfOpen
    /// </summary>
    public int HalfOpenSuccessThreshold { get; set; } = 2;

    // Navigation
    public LLMProvider? Provider { get; set; }

    public bool IsOpen => State == "Open" && (NextResetAt == null || DateTime.UtcNow < NextResetAt);
    public bool IsHalfOpen => State == "HalfOpen";
    public bool IsClosed => State == "Closed";
}

/// <summary>
/// Task checkpoint (periodic progress saving for resumption)
/// </summary>
public class TaskCheckpoint
{
    public int Id { get; set; }
    public int TaskId { get; set; }

    /// <summary>
    /// Checkpoint name/label (e.g., "AfterSkillExecution", "BeforeToolCall")
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Serialized state at this checkpoint (JSON)
    /// </summary>
    public string? StateJson { get; set; }

    /// <summary>
    /// Iteration count if inside a tool loop
    /// </summary>
    public int Iteration { get; set; }

    /// <summary>
    /// Partial output accumulated so far
    /// </summary>
    public string? PartialOutput { get; set; }

    /// <summary>
    /// Tokens used up to this checkpoint
    /// </summary>
    public int TokensUsed { get; set; }

    /// <summary>
    /// Cost incurred up to this checkpoint
    /// </summary>
    public decimal CostUSD { get; set; }

    /// <summary>
    /// Whether this is the final/recovery checkpoint
    /// </summary>
    public bool IsRecoverable { get; set; } = true;

    /// <summary>
    /// When checkpoint was saved
    /// </summary>
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public AgentTask? Task { get; set; }
}

/// <summary>
/// Summary of resilience state for reporting
/// </summary>
public class ProviderResilienceStatus
{
    public int ProviderId { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string CircuitState { get; set; } = "Closed";
    public int ConsecutiveFailures { get; set; }
    public int SuccessLast24h { get; set; }
    public int FailuresLast24h { get; set; }
    public double SuccessRateLast24h { get; set; }
    public DateTime? CircuitOpenedAt { get; set; }
    public DateTime? CircuitResetAt { get; set; }
    public bool IsAvailable => CircuitState != "Open";
    public DateTime AsOfDate { get; set; } = DateTime.UtcNow;
}
