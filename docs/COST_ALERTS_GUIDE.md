# Cost Alerts & Escalation Guide

This document describes the alert notification and escalation system for Xpedeon Agent Mission Control, enabling proactive budget management through multi-channel notifications.

## Overview

- **Purpose**: Notify users when agent spending reaches budget thresholds
- **Scope**: Per-agent alerts at 50%, 75%, 90%, and 100% of budget
- **Channels**: Email, Slack, PagerDuty (extensible)
- **Escalation**: Configurable actions per threshold (notify, throttle, block, queue)
- **Management**: Acknowledge, dismiss, and track alert history

## Architecture

```
CostTrackingService.RecordCostAsync()
    ↓
CostTrackingService.GenerateAlertsAsync()
    ├─ Check daily threshold (50%, 75%, 90%, 100%)
    ├─ Check monthly threshold (50%, 75%, 90%, 100%)
    └─ Create CostAlert if threshold hit
        ↓
BackgroundAlertProcessorService (every 30 seconds)
    ├─ Query unnotified CostAlerts
    └─ For each alert:
        ├─ Fetch AlertNotificationConfig for agent
        ├─ Check AlertEscalationRule
        ├─ Verify notification is enabled for threshold
        ├─ Check duplicate alert prevention
        └─ Send via configured channels (Email, Slack)
            ├─ EmailAlertHandler
            └─ SlackAlertHandler
                ↓
            Record AlertNotificationHistory
            Update CostAlert.IsNotified = true
```

## Notification Configuration

### Configure Alert Channels

```csharp
public class AgentService
{
    private readonly AlertNotificationService _alertService;

    public async Task ConfigureAlertsAsync(int agentId, ConfigureAlertsRequest request)
    {
        await _alertService.ConfigureNotificationsAsync(
            agentId: agentId,
            emailAddress: request.Email,        // e.g., "ops@company.com"
            slackWebhookUrl: request.SlackUrl,  // e.g., "https://hooks.slack.com/..."
            slackChannelName: request.Channel   // e.g., "#alerts"
        );
    }
}
```

### AlertNotificationConfig

Per-agent notification settings:

```csharp
public class AlertNotificationConfig {
    int Id;
    int AgentId;
    string? EmailAddress;             // Where to send email alerts
    string? SlackWebhookUrl;          // Slack incoming webhook URL
    string? SlackChannelName;         // Slack channel name for reference
    string? PagerDutyKey;             // PagerDuty integration (future)
    bool NotifyAt50Percent;           // Alert at 50% of budget (default: true)
    bool NotifyAt75Percent;           // Alert at 75% of budget (default: true)
    bool NotifyAt90Percent;           // Alert at 90% of budget (default: true)
    bool NotifyAt100Percent;          // Alert at 100% (exceeded) (default: true)
    int MinutesBeforeDuplicateAlert;  // Prevent spam (default: 60 min)
    DateTime CreatedAt;
    DateTime UpdatedAt;
}
```

## Escalation Rules

### Define Actions Per Threshold

```csharp
public class AlertEscalationRule {
    int Id;
    int AgentId;
    int ThresholdPercent;             // 50, 75, 90, 100
    string Period;                    // "Daily" or "Monthly"
    string Action;                    // "Notify", "Notify+Throttle", "Block", "Queue"
    string? MessageTemplate;          // Custom message (optional)
    bool IsActive;                    // Rule enabled/disabled
    DateTime CreatedAt;
}
```

### Escalation Actions

1. **Notify** — Send notification only
   ```
   At 50%: Informational alert sent
   At 75%: Warning alert sent
   At 90%: Critical alert sent
   At 100%: Budget exceeded alert sent
   ```

2. **Notify+Throttle** — Notify + apply rate limiting
   ```
   At 90%: Send alert + reduce CallsPerMinute to 50%
   At 100%: Block all execution
   ```

3. **Block** — Prevent execution
   ```
   At 100%: Task execution blocked
   ```

4. **Queue** — Queue instead of blocking
   ```
   At 100%: Task queued instead of rejected
   ```

### Setup Escalation Rules

```csharp
// In database migration or initialization
var rules = new[]
{
    // Daily limits
    new AlertEscalationRule
    {
        AgentId = 42,
        ThresholdPercent = 50,
        Period = "Daily",
        Action = "Notify",
        MessageTemplate = "Daily budget at {Percent}%: ${Current} / ${Limit}",
        IsActive = true
    },
    new AlertEscalationRule
    {
        AgentId = 42,
        ThresholdPercent = 90,
        Period = "Daily",
        Action = "Notify+Throttle",
        IsActive = true
    },
    new AlertEscalationRule
    {
        AgentId = 42,
        ThresholdPercent = 100,
        Period = "Daily",
        Action = "Block",
        IsActive = true
    },
    // Monthly limits
    new AlertEscalationRule
    {
        AgentId = 42,
        ThresholdPercent = 75,
        Period = "Monthly",
        Action = "Notify",
        IsActive = true
    }
};

context.AlertEscalationRules.AddRange(rules);
context.SaveChanges();
```

## Alert Examples

### Email Alert

```
FROM: alerts@xpedeon.com
TO: ops@company.com
SUBJECT: Budget Alert - Agent #42

🚨 Critical
Budget Alert for Agent #42

Period: Monthly
Threshold: 90%

Current Spending: $2,700.00
Budget Limit: $3,000.00
Usage: 90%

Message: Monthly budget at 90% threshold

Time: 2026-04-30 14:30:00 UTC
```

### Slack Alert

```
:warning: Critical
Budget Alert for Agent #42

Period: Monthly
Threshold: 90%

Current Spending: $2,700.00
Budget Limit: $3,000.00
Usage: 90%

Message: Monthly budget at 90% threshold

⏰ 2026-04-30 14:30:00 UTC
```

## Notification Channels

### Email

Prerequisites:
- Email service configured (SendGrid, AWS SES, etc.)
- Recipient email address stored in AlertNotificationConfig.EmailAddress

Implementation:
```csharp
public class EmailAlertHandler : IAlertNotificationHandler
{
    public async Task<bool> SendAsync(string recipient, string message)
    {
        // TODO: Integrate with your email service
        var emailService = new EmailService();
        return await emailService.SendAsync(
            to: recipient,
            subject: "Agent Budget Alert",
            body: message
        );
    }
}
```

### Slack

Prerequisites:
- Slack workspace with incoming webhooks enabled
- Webhook URL from Slack app configuration

Implementation:
```csharp
public class SlackAlertHandler : IAlertNotificationHandler
{
    public async Task<bool> SendAsync(string recipient, string message)
    {
        var webhook = recipient; // e.g., "https://hooks.slack.com/services/..."
        var payload = new { text = message, ... };
        var response = await httpClient.PostAsJsonAsync(webhook, payload);
        return response.IsSuccessStatusCode;
    }
}
```

### Extending to Other Channels

```csharp
public class PagerDutyAlertHandler : IAlertNotificationHandler
{
    public async Task<bool> SendAsync(string recipient, string message)
    {
        // recipient = PagerDuty integration key
        var incident = new
        {
            title = "Agent Budget Alert",
            body = message,
            severity = "critical"
        };
        return await PostToPagerDutyAsync(recipient, incident);
    }
}

// Register in AlertNotificationService
_handlers["PagerDuty"] = new PagerDutyAlertHandler(...);
```

## Duplicate Alert Prevention

Prevents alert spam by tracking recent notifications:

```csharp
// Config: MinutesBeforeDuplicateAlert = 60 (default)

// First alert at 75% threshold → SENT
// Second alert at 75% within 60 minutes → SKIPPED (duplicate)
// Third alert at 75% after 60+ minutes → SENT (new alert cycle)

// Different threshold (75% → 90%) → Always sent (not a duplicate)
```

Configuration:
```csharp
var config = new AlertNotificationConfig
{
    AgentId = 42,
    EmailAddress = "ops@company.com",
    MinutesBeforeDuplicateAlert = 60  // Adjust spam tolerance
};
```

## Alert Management

### Acknowledge Alert

User marks alert as acknowledged:

```csharp
await _alertService.AcknowledgeAlertAsync(
    costAlertId: 123,
    acknowledgedBy: "john.doe@company.com",
    actionTaken: "Increased monthly budget to $5000",
    notes: "Customer requested higher tier service"
);
```

Creates AlertAcknowledgment:
- Links acknowledgment to CostAlert
- Records who acknowledged and when
- Stores action taken and notes
- Logs to AuditLogs

### Track Alert History

```csharp
// Query alert notifications sent
var history = await db.AlertNotificationHistories
    .Where(h => h.AgentId == 42)
    .OrderByDescending(h => h.SentAt)
    .ToListAsync();

foreach (var entry in history)
{
    Console.WriteLine($"{entry.NotificationType} to {entry.Recipient}");
    Console.WriteLine($"  Threshold: {entry.ThresholdPercent}%");
    Console.WriteLine($"  Sent: {entry.WasSent}");
    if (!entry.WasSent)
        Console.WriteLine($"  Error: {entry.ErrorMessage}");
}
```

### View Alert Status

```csharp
// Get unacknowledged alerts
var pendingAlerts = await db.CostAlerts
    .Where(a => a.AcknowledgedAt == null)
    .ToListAsync();

// Get acknowledged alerts
var resolved = await db.CostAlerts
    .Where(a => a.AcknowledgedAt != null)
    .Include(a => a.AlertAcknowledgment)
    .ToListAsync();
```

## Monitoring

### Metrics

```prometheus
xpedeon_cost_alerts_created_total{agent_id="1", threshold="75"} 12
xpedeon_cost_alerts_sent_total{agent_id="1", channel="slack"} 10
xpedeon_cost_alerts_failed_total{agent_id="1"} 2
xpedeon_cost_alerts_acknowledged_total{agent_id="1"} 8
xpedeon_alert_notification_delay_seconds{percentile="p95"} 15
```

### Alerts

```yaml
- name: AlertNotificationFailure
  condition: rate(xpedeon_cost_alerts_failed_total[5m]) > 0
  severity: warning
  for: 5m
  action: Check AlertNotificationService logs

- name: HighAlertVolume
  condition: rate(xpedeon_cost_alerts_created_total[1h]) > 10
  severity: info
  for: 10m
  action: Review budget limits, may be too restrictive
```

## Testing

### Unit Test: Alert Notification

```csharp
[Fact]
public async Task ProcessPendingAlertsAsync_SendsEmailAndSlackNotifications()
{
    var agentId = 1;

    // Setup config
    var config = new AlertNotificationConfig
    {
        AgentId = agentId,
        EmailAddress = "test@company.com",
        SlackWebhookUrl = "https://hooks.slack.com/...",
        NotifyAt75Percent = true
    };
    await db.AlertNotificationConfigs.AddAsync(config);

    // Create an unnotified alert
    var alert = new CostAlert
    {
        AgentId = agentId,
        ThresholdPercent = 75,
        Period = "Monthly",
        CurrentSpentUSD = 2250,
        LimitUSD = 3000,
        Message = "Monthly budget at 75%",
        IsNotified = false
    };
    await db.CostAlerts.AddAsync(alert);
    await db.SaveChangesAsync();

    // Process alerts
    var processed = await _alertService.ProcessPendingAlertsAsync();
    Assert.True(processed);

    // Verify notification history
    var notifications = await db.AlertNotificationHistories
        .Where(h => h.CostAlertId == alert.Id)
        .ToListAsync();

    Assert.Collection(notifications,
        n => Assert.Equal("Email", n.NotificationType),
        n => Assert.Equal("Slack", n.NotificationType)
    );

    // Verify alert marked as notified
    alert = await db.CostAlerts.FindAsync(alert.Id);
    Assert.True(alert.IsNotified);
}
```

### Integration Test: Escalation

```csharp
[Fact]
public async Task CostTracking_TriggersEscalationRules()
{
    var agentId = 1;
    var monthlyLimit = 1000m;

    // Setup escalation rule: Block at 100%
    var rule = new AlertEscalationRule
    {
        AgentId = agentId,
        ThresholdPercent = 100,
        Period = "Monthly",
        Action = "Block",
        IsActive = true
    };
    await db.AlertEscalationRules.AddAsync(rule);

    // Initialize budget
    await _costTrackingService.InitializeBudgetAsync(agentId, null, monthlyLimit);

    // Record costs up to limit
    for (int i = 0; i < 10; i++)
    {
        await _costTrackingService.RecordCostAsync(
            agentId, i, monthlyLimit / 10, 1000
        );
    }

    // Verify 100% alert was created
    var alerts = await db.CostAlerts
        .Where(a => a.AgentId == agentId && a.ThresholdPercent == 100)
        .ToListAsync();

    Assert.NotEmpty(alerts);

    // Verify budget check now fails
    var budgetOk = await _costTrackingService.CheckBudgetAsync(agentId, 1m);
    Assert.False(budgetOk);
}
```

## Best Practices

1. **Configure Early** — Set up notification channels when agent is created
2. **Define Rules** — Establish escalation rules matching your risk tolerance
3. **Adjust Thresholds** — Tune duplicate alert prevention based on usage patterns
4. **Monitor Failures** — Check alert notification failures regularly
5. **Test Channels** — Verify email/Slack connectivity during setup
6. **Acknowledge Alerts** — Document actions taken for compliance
7. **Review Trends** — Analyze alert history to optimize budget limits
8. **Coordinate Escalation** — Align escalation rules with budget enforcement

## Troubleshooting

### Alerts Not Sending

**Check:**
1. Is AlertNotificationConfig set up for agent?
2. Are notification thresholds enabled (NotifyAt50Percent, etc.)?
3. Is BackgroundAlertProcessorService running?
4. Check AlertNotificationHistories for errors
5. Verify webhook URLs are correct

### Too Many Alerts

**Solutions:**
1. Increase MinutesBeforeDuplicateAlert
2. Disable lower thresholds (e.g., disable NotifyAt50Percent)
3. Adjust budget limits if too restrictive
4. Use Notify+Throttle instead of Block

### Wrong Alert Message

**Check:**
1. Is custom MessageTemplate set in AlertEscalationRule?
2. Check template variables: {Agent}, {Percent}, {Current}, {Limit}
3. Verify message is being rendered correctly

## References

- COST_TRACKING_GUIDE.md — Budget models and tracking
- RATE_LIMITING_GUIDE.md — Rate limit policies
- PRODUCTION_ROADMAP.md — Phase 2 requirements
- Models/AlertNotificationModels.cs — Data models
- Services/AlertNotificationService.cs — Notification logic
- Services/BackgroundAlertProcessorService.cs — Background processing
