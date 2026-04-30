namespace XpedeonAgentMissionControl.Models;

/// <summary>
/// Cost and budget tracking models for spend management
/// </summary>

/// <summary>
/// Budget limits for an agent (daily/monthly spending caps)
/// </summary>
public class AgentBudget
{
    public int Id { get; set; }
    public int AgentId { get; set; }

    /// <summary>
    /// Daily spending limit in USD (null = unlimited)
    /// </summary>
    public decimal? DailyCostLimitUSD { get; set; }

    /// <summary>
    /// Monthly spending limit in USD (null = unlimited)
    /// </summary>
    public decimal? MonthlyCostLimitUSD { get; set; }

    /// <summary>
    /// Percentage threshold (50, 75, 90) for alerts
    /// </summary>
    public int AlertThresholdPercent { get; set; } = 75;

    /// <summary>
    /// Total spent today (USD)
    /// </summary>
    public decimal TodaySpentUSD { get; set; }

    /// <summary>
    /// Total spent this month (USD)
    /// </summary>
    public decimal MonthSpentUSD { get; set; }

    /// <summary>
    /// Projected end-of-month spending (linear extrapolation)
    /// </summary>
    public decimal ProjectedMonthlyUSD { get; set; }

    /// <summary>
    /// Last time budget was checked/reset
    /// </summary>
    public DateTime LastCheckedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When today's budget resets (next UTC midnight)
    /// </summary>
    public DateTime DailyResetAt { get; set; }

    /// <summary>
    /// When month's budget resets (next month, 1st UTC)
    /// </summary>
    public DateTime MonthlyResetAt { get; set; }

    // Navigation
    public Agent? Agent { get; set; }

    // Computed properties
    public bool HasDailyLimit => DailyCostLimitUSD.HasValue && DailyCostLimitUSD > 0;
    public bool HasMonthlyLimit => MonthlyCostLimitUSD.HasValue && MonthlyCostLimitUSD > 0;
    public bool IsDailyLimitExceeded => HasDailyLimit && TodaySpentUSD >= DailyCostLimitUSD;
    public bool IsMonthlyLimitExceeded => HasMonthlyLimit && MonthSpentUSD >= MonthlyCostLimitUSD;
    public decimal DailyRemainingUSD => HasDailyLimit ? DailyCostLimitUSD!.Value - TodaySpentUSD : decimal.MaxValue;
    public decimal MonthlyRemainingUSD => HasMonthlyLimit ? MonthlyCostLimitUSD!.Value - MonthSpentUSD : decimal.MaxValue;
    public int DailyUsagePercent => HasDailyLimit ? (int)((TodaySpentUSD / DailyCostLimitUSD!.Value) * 100) : 0;
    public int MonthlyUsagePercent => HasMonthlyLimit ? (int)((MonthSpentUSD / MonthlyCostLimitUSD!.Value) * 100) : 0;
}

/// <summary>
/// Cost event tracking (when an agent incurs a cost)
/// </summary>
public class CostEvent
{
    public int Id { get; set; }
    public int AgentId { get; set; }
    public int TaskId { get; set; }

    /// <summary>
    /// Cost incurred (USD)
    /// </summary>
    public decimal CostUSD { get; set; }

    /// <summary>
    /// Tokens used in this task
    /// </summary>
    public int TokensUsed { get; set; }

    /// <summary>
    /// Input tokens (if breakdown available)
    /// </summary>
    public int? InputTokens { get; set; }

    /// <summary>
    /// Output tokens (if breakdown available)
    /// </summary>
    public int? OutputTokens { get; set; }

    /// <summary>
    /// LLM provider used
    /// </summary>
    public string? ProviderName { get; set; }

    /// <summary>
    /// Model used (e.g., "claude-3-sonnet")
    /// </summary>
    public string? ModelName { get; set; }

    /// <summary>
    /// When cost was incurred
    /// </summary>
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Agent? Agent { get; set; }
    public AgentTask? Task { get; set; }
}

/// <summary>
/// Cost threshold alert (when budget hit 50%, 75%, 90%)
/// </summary>
public class CostAlert
{
    public int Id { get; set; }
    public int AgentId { get; set; }

    /// <summary>
    /// Threshold that was hit (50, 75, 90, 100)
    /// </summary>
    public int ThresholdPercent { get; set; }

    /// <summary>
    /// Period: "Daily" or "Monthly"
    /// </summary>
    public string Period { get; set; } = string.Empty;

    /// <summary>
    /// Current spending (USD)
    /// </summary>
    public decimal CurrentSpentUSD { get; set; }

    /// <summary>
    /// Budget limit (USD)
    /// </summary>
    public decimal LimitUSD { get; set; }

    /// <summary>
    /// Alert message
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Whether alert was already sent to user
    /// </summary>
    public bool IsNotified { get; set; }

    /// <summary>
    /// When alert was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When alert was acknowledged/dismissed by user
    /// </summary>
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>
    /// Who acknowledged the alert
    /// </summary>
    public string? AcknowledgedBy { get; set; }

    // Navigation
    public Agent? Agent { get; set; }

    public bool IsAcknowledged => AcknowledgedAt.HasValue;
}

/// <summary>
/// Budget override (admin allows spending beyond limit)
/// </summary>
public class BudgetOverride
{
    public int Id { get; set; }
    public int AgentId { get; set; }

    /// <summary>
    /// Additional amount allowed (USD)
    /// </summary>
    public decimal OverrideAmountUSD { get; set; }

    /// <summary>
    /// Period: "Daily" or "Monthly"
    /// </summary>
    public string Period { get; set; } = string.Empty;

    /// <summary>
    /// Reason for override (required for audit)
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Admin who approved override
    /// </summary>
    public string ApprovedBy { get; set; } = string.Empty;

    /// <summary>
    /// Ticket/incident reference
    /// </summary>
    public string? TicketRef { get; set; }

    /// <summary>
    /// When override expires (null = permanent)
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// When override was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Agent? Agent { get; set; }

    public bool IsActive => ExpiresAt == null || ExpiresAt > DateTime.UtcNow;
}

/// <summary>
/// Cost summary for reporting
/// </summary>
public class CostSummary
{
    public int AgentId { get; set; }
    public decimal TodaySpentUSD { get; set; }
    public decimal MonthSpentUSD { get; set; }
    public decimal ProjectedMonthlyUSD { get; set; }
    public decimal DailyLimitUSD { get; set; }
    public decimal MonthlyLimitUSD { get; set; }
    public int DailyUsagePercent { get; set; }
    public int MonthlyUsagePercent { get; set; }
    public DateTime AsOfDate { get; set; } = DateTime.UtcNow;
}
