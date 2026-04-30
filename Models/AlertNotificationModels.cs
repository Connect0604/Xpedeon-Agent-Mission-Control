namespace XpedeonAgentMissionControl.Models;

/// <summary>
/// Alert notification configuration
/// </summary>
public class AlertNotificationConfig
{
    public int Id { get; set; }
    public int AgentId { get; set; }

    /// <summary>
    /// Email address to notify (optional)
    /// </summary>
    public string? EmailAddress { get; set; }

    /// <summary>
    /// Slack webhook URL (optional)
    /// </summary>
    public string? SlackWebhookUrl { get; set; }

    /// <summary>
    /// Slack channel name (optional, e.g., "#alerts")
    /// </summary>
    public string? SlackChannelName { get; set; }

    /// <summary>
    /// PagerDuty integration key (optional)
    /// </summary>
    public string? PagerDutyKey { get; set; }

    /// <summary>
    /// Whether to send alerts for 50% threshold
    /// </summary>
    public bool NotifyAt50Percent { get; set; } = true;

    /// <summary>
    /// Whether to send alerts for 75% threshold
    /// </summary>
    public bool NotifyAt75Percent { get; set; } = true;

    /// <summary>
    /// Whether to send alerts for 90% threshold
    /// </summary>
    public bool NotifyAt90Percent { get; set; } = true;

    /// <summary>
    /// Whether to send alerts for 100% (budget exceeded)
    /// </summary>
    public bool NotifyAt100Percent { get; set; } = true;

    /// <summary>
    /// Minimum time between alerts for same threshold (prevents spam)
    /// </summary>
    public int MinutesBeforeDuplicateAlert { get; set; } = 60;

    /// <summary>
    /// When created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Agent? Agent { get; set; }
}

/// <summary>
/// Alert escalation rule (what to do at each threshold)
/// </summary>
public class AlertEscalationRule
{
    public int Id { get; set; }
    public int AgentId { get; set; }

    /// <summary>
    /// Threshold percent: 50, 75, 90, 100
    /// </summary>
    public int ThresholdPercent { get; set; }

    /// <summary>
    /// Period: "Daily" or "Monthly"
    /// </summary>
    public string Period { get; set; } = string.Empty;

    /// <summary>
    /// Action to take: "Notify", "Notify+Throttle", "Block", "Queue"
    /// </summary>
    public string Action { get; set; } = "Notify";

    /// <summary>
    /// Message template (can use {Agent}, {Threshold}, {Current}, {Limit}, {Percent})
    /// </summary>
    public string? MessageTemplate { get; set; }

    /// <summary>
    /// Whether this rule is active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When rule was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Agent? Agent { get; set; }
}

/// <summary>
/// Alert notification history (tracking sent alerts)
/// </summary>
public class AlertNotificationHistory
{
    public int Id { get; set; }
    public int AgentId { get; set; }
    public int CostAlertId { get; set; }

    /// <summary>
    /// Type of alert: "Email", "Slack", "PagerDuty"
    /// </summary>
    public string NotificationType { get; set; } = string.Empty;

    /// <summary>
    /// Recipient (email address, Slack channel, etc.)
    /// </summary>
    public string Recipient { get; set; } = string.Empty;

    /// <summary>
    /// Alert threshold that triggered notification
    /// </summary>
    public int ThresholdPercent { get; set; }

    /// <summary>
    /// Message sent
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Whether notification was successfully sent
    /// </summary>
    public bool WasSent { get; set; }

    /// <summary>
    /// Error message if send failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// When notification was sent
    /// </summary>
    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Agent? Agent { get; set; }
    public CostAlert? CostAlert { get; set; }
}

/// <summary>
/// Alert acknowledgment (user acknowledges receipt of alert)
/// </summary>
public class AlertAcknowledgment
{
    public int Id { get; set; }
    public int CostAlertId { get; set; }

    /// <summary>
    /// User who acknowledged
    /// </summary>
    public string AcknowledgedBy { get; set; } = string.Empty;

    /// <summary>
    /// Action taken (e.g., "Increased budget", "Accepted alert")
    /// </summary>
    public string ActionTaken { get; set; } = string.Empty;

    /// <summary>
    /// Notes
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// When acknowledged
    /// </summary>
    public DateTime AcknowledgedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public CostAlert? CostAlert { get; set; }
}
