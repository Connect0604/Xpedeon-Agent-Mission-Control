# Cost Tracking Guide

This document describes the cost tracking and budget enforcement system for Xpedeon Agent Mission Control, including budget management, cost recording, alerts, and compliance.

## Overview

- **Purpose**: Track LLM usage costs per agent and enforce spending limits
- **Scope**: Daily and monthly budget limits, per-agent cost tracking
- **Enforcement**: Prevents task execution if budget exceeded
- **Alerts**: Automatic notifications at 50%, 75%, 90%, and 100% of budget
- **Overrides**: Admin-approved budget overrides with audit trail

## Architecture

```
Agent Task Execution
    ↓
LLMExecutionService.ExecuteAsync()
    ↓
CostTrackingService.CheckBudgetAsync()
    ├─ Daily limit check
    ├─ Monthly limit check
    └─ Active override check
    ↓
[Budget OK] → Execute task → RecordCostAsync()
    ↓
Update AgentBudget + Create CostEvent
    ↓
GenerateAlertsAsync() → Create CostAlerts if thresholds hit
    ↓
Log to AuditLogs
```

## Cost Models

### AgentBudget

Stores per-agent daily and monthly spending limits and current usage:

```csharp
public class AgentBudget {
    int Id;                           // Primary key
    int AgentId;                      // FK to Agent
    decimal? DailyCostLimitUSD;       // Daily spending cap (null = unlimited)
    decimal? MonthlyCostLimitUSD;     // Monthly spending cap (null = unlimited)
    int AlertThresholdPercent;        // Alert trigger (default: 75%)
    decimal TodaySpentUSD;            // Current daily spending
    decimal MonthSpentUSD;            // Current monthly spending
    decimal ProjectedMonthlyUSD;      // Linear extrapolation to EOMonth
    DateTime LastCheckedAt;           // Last budget check time
    DateTime DailyResetAt;            // Next UTC midnight
    DateTime MonthlyResetAt;          // Next UTC 1st of month
}
```

### CostEvent

Records individual cost transactions:

```csharp
public class CostEvent {
    int Id;                           // Primary key
    int AgentId;                      // FK to Agent
    int TaskId;                       // FK to AgentTask
    decimal CostUSD;                  // Cost incurred ($)
    int TokensUsed;                   // Total tokens consumed
    int? InputTokens;                 // Prompt tokens (if available)
    int? OutputTokens;                // Completion tokens (if available)
    string ProviderName;              // "Claude", "OpenAI", etc.
    string ModelName;                 // "claude-3-sonnet"
    DateTime OccurredAt;              // Transaction timestamp (UTC)
}
```

### CostAlert

Tracks budget threshold alerts:

```csharp
public class CostAlert {
    int Id;                           // Primary key
    int AgentId;                      // FK to Agent
    int ThresholdPercent;             // 50, 75, 90, 100
    string Period;                    // "Daily" or "Monthly"
    decimal CurrentSpentUSD;          // Spending at alert time
    decimal LimitUSD;                 // Budget limit
    string Message;                   // Alert message
    bool IsNotified;                  // Whether alert was sent
    DateTime CreatedAt;               // Alert timestamp
    DateTime? AcknowledgedAt;         // User acknowledgment time
    string AcknowledgedBy;            // User who acknowledged
}
```

### BudgetOverride

Admin-approved spending overrides:

```csharp
public class BudgetOverride {
    int Id;                           // Primary key
    int AgentId;                      // FK to Agent
    decimal OverrideAmountUSD;        // Additional allowed spending
    string Period;                    // "Daily" or "Monthly"
    string Reason;                    // Audit-required reason
    string ApprovedBy;                // Admin who approved
    string TicketRef;                 // Incident/ticket reference
    DateTime? ExpiresAt;              // Override expiration (null = permanent)
    DateTime CreatedAt;               // Override timestamp
}
```

## Usage

### Initialize Agent Budget

```csharp
public class AgentService
{
    private readonly CostTrackingService _costTrackingService;

    public async Task<Agent> CreateAgentAsync(CreateAgentRequest request)
    {
        var agent = new Agent
        {
            Name = request.Name,
            LLMProviderId = request.LLMProviderId,
            // ...
        };

        await _agentRepository.SaveAsync(agent);

        // Initialize budget for the agent
        await _costTrackingService.InitializeBudgetAsync(
            agentId: agent.Id,
            dailyLimitUSD: 100.00m,      // $100/day limit
            monthlyLimitUSD: 3000.00m    // $3000/month limit
        );

        return agent;
    }
}
```

### Check Budget Before Execution

```csharp
public class LLMExecutionService
{
    private readonly CostTrackingService _costTrackingService;

    public async Task<string> ExecuteAsync(Agent agent, string input)
    {
        // Estimate cost (based on prompt length heuristic)
        var estimatedCostUSD = EstimateCost(agent.LLMProvider, input);

        // Check if budget allows this execution
        var budgetOk = await _costTrackingService.CheckBudgetAsync(
            agentId: agent.Id,
            requestedCostUSD: estimatedCostUSD
        );

        if (!budgetOk)
            throw new BudgetExceededException($"Budget exceeded for agent {agent.Id}");

        // Execute LLM call
        var response = await CallLLMAsync(agent, input);

        // Record actual cost
        await _costTrackingService.RecordCostAsync(
            agentId: agent.Id,
            taskId: task.Id,
            costUSD: response.ActualCostUSD,
            tokensUsed: response.TotalTokens,
            inputTokens: response.InputTokens,
            outputTokens: response.OutputTokens,
            providerName: agent.LLMProvider.Name,
            modelName: agent.LLMProvider.ModelName
        );

        return response.Output;
    }
}
```

### Get Cost Summary

```csharp
// Dashboard widget: show current spending
var summary = await _costTrackingService.GetCostSummaryAsync(agentId: 42);

Console.WriteLine($"Today: ${summary.TodaySpentUSD:F2} / ${summary.DailyLimitUSD:F2} ({summary.DailyUsagePercent}%)");
Console.WriteLine($"Month: ${summary.MonthSpentUSD:F2} / ${summary.MonthlyLimitUSD:F2} ({summary.MonthlyUsagePercent}%)");
Console.WriteLine($"Projected: ${summary.ProjectedMonthlyUSD:F2}");
```

### Apply Budget Override

```csharp
// Admin approves additional spending for an agent
await _costTrackingService.ApplyOverrideAsync(
    agentId: 42,
    overrideAmountUSD: 500.00m,
    period: "Monthly",
    reason: "High-priority customer project requires additional compute",
    approvedBy: "admin@company.com",
    ticketRef: "INC-12345",
    expiresAt: DateTime.UtcNow.AddDays(30)  // 30-day temporary override
);
```

## Cost Calculation

### Estimating Cost

Cost is calculated based on token counts and provider pricing:

```csharp
decimal CalculateCost(LLMProvider provider, int inputTokens, int outputTokens)
{
    var inputCost = (inputTokens / 1000m) * provider.CostPer1kInputTokens;
    var outputCost = (outputTokens / 1000m) * provider.CostPer1kOutputTokens;
    return inputCost + outputCost;
}

// Example: Claude 3 Sonnet
// Input: $3 per 1M tokens = $0.003 per 1k tokens
// Output: $15 per 1M tokens = $0.015 per 1k tokens

var inputTokens = 1000;    // 1k input tokens
var outputTokens = 500;    // 500 output tokens
var cost = (1000 / 1000m) * 0.003m + (500 / 1000m) * 0.015m;
// cost = $0.003 + $0.0075 = $0.0105 ≈ $0.01
```

### Projected Monthly Spending

Linear extrapolation from current day:

```csharp
var dayOfMonth = DateTime.UtcNow.Day;           // 1-31
var daysInMonth = DateTime.DaysInMonth(year, month); // 28-31

decimal projectedMonthlyUSD = (budget.MonthSpentUSD / dayOfMonth) * daysInMonth;

// Example: On day 10, spent $300
// Projected = ($300 / 10) * 30 = $900 for the month
```

## Budget Enforcement

### Budget Limits

1. **Daily Limit** — Resets at UTC midnight (00:00)
2. **Monthly Limit** — Resets on 1st of month (00:00 UTC)
3. **No Limit** — Set to `null` for unlimited

### Enforcement Points

```csharp
// Before execution: CheckBudgetAsync()
if (!await _costTrackingService.CheckBudgetAsync(agentId, estimatedCost))
    throw new BudgetExceededException(); // Task blocked

// After execution: RecordCostAsync()
await _costTrackingService.RecordCostAsync(agentId, taskId, actualCost, ...);
// Updates AgentBudget, creates CostEvent, generates alerts
```

### Active Overrides

If an active `BudgetOverride` exists for an agent, budget checks pass (override allows spending).

Overrides can be:
- **Temporary** — `ExpiresAt` set to future date
- **Permanent** — `ExpiresAt` is null

## Alerts

### Alert Thresholds

Alerts are created when budget usage reaches:
- **50%** — Warning: halfway to limit
- **75%** — Caution: most of budget spent
- **90%** — Critical: nearly exhausted
- **100%** — Error: budget exceeded (task blocked)

### Alert Properties

- One alert per threshold per day (duplicates not created)
- `IsNotified` flag: tracks whether alert was sent to user
- `AcknowledgedAt` / `AcknowledgedBy`: manual acknowledgment
- Includes current spending, limit, and percentage

### Alert Flow

```
RecordCostAsync()
    ↓
GenerateAlertsAsync()
    ├─ Check daily usage %
    ├─ Check monthly usage %
    ├─ For each threshold (50, 75, 90, 100):
    │   └─ If usage >= threshold AND no alert today:
    │       └─ Create CostAlert
    │           └─ Send notification (email, Slack, etc.)
    └─ Log to AuditLogs
```

## Compliance & Audit

### GDPR Compliance

Cost data is associated with agents and tasks, not personal data. No special erasure handling needed beyond normal data deletion.

### SOC 2 Compliance

- ✅ All cost changes logged with timestamp, user, reason
- ✅ Budget overrides require approval and audit trail
- ✅ Monthly audit reports available via queries

### Audit Trail

Each cost action creates an AuditLog entry:

```json
{
  "action": "RecordCost",
  "entityType": "CostEvent",
  "entityId": "5001",
  "userId": "system",
  "timestamp": "2026-04-30T14:30:00Z",
  "changeDescription": "Cost $0.05 recorded (250 tokens)"
}
```

```json
{
  "action": "ApplyBudgetOverride",
  "entityType": "BudgetOverride",
  "entityId": "201",
  "userId": "admin@company.com",
  "changeDescription": "Override $500 applied (Monthly) by admin@company.com"
}
```

## Testing

### Unit Test: Budget Enforcement

```csharp
[Fact]
public async Task CheckBudgetAsync_BlocksExecutionWhenLimitExceeded()
{
    var agentId = 1;
    var dailyLimit = 100.00m;

    // Initialize budget with $100/day limit
    await _costTrackingService.InitializeBudgetAsync(agentId, dailyLimit, null);

    // Record $80 cost
    await _costTrackingService.RecordCostAsync(
        agentId: agentId,
        taskId: 1,
        costUSD: 80.00m,
        tokensUsed: 5000
    );

    // Check if $50 more is allowed: 80 + 50 > 100 → false
    var budgetOk = await _costTrackingService.CheckBudgetAsync(agentId, 50.00m);
    Assert.False(budgetOk);

    // Check if $20 more is allowed: 80 + 20 <= 100 → true
    budgetOk = await _costTrackingService.CheckBudgetAsync(agentId, 20.00m);
    Assert.True(budgetOk);
}
```

### Integration Test: Alert Generation

```csharp
[Fact]
public async Task RecordCostAsync_GeneratesAlertAt75Percent()
{
    var agentId = 1;
    var monthlyLimit = 1000.00m;

    await _costTrackingService.InitializeBudgetAsync(agentId, null, monthlyLimit);

    // Record costs to reach 75% of monthly budget
    var costPerEvent = monthlyLimit * 0.25m; // 3 events = 75%

    for (int i = 1; i <= 3; i++)
    {
        await _costTrackingService.RecordCostAsync(
            agentId: agentId,
            taskId: i,
            costUSD: costPerEvent,
            tokensUsed: 1000
        );
    }

    // Should have created alerts at 50% and 75%
    using (var db = _dbContextFactory.CreateDbContext())
    {
        var alerts = await db.CostAlerts
            .Where(a => a.AgentId == agentId && a.Period == "Monthly")
            .OrderBy(a => a.ThresholdPercent)
            .ToListAsync();

        Assert.Collection(alerts,
            a => Assert.Equal(50, a.ThresholdPercent),
            a => Assert.Equal(75, a.ThresholdPercent)
        );
    }
}
```

## Monitoring

### Cost Metrics

```prometheus
xpedeon_agent_daily_spent{agent_id="1"} 45.67
xpedeon_agent_daily_limit{agent_id="1"} 100.00
xpedeon_agent_daily_usage_percent{agent_id="1"} 45

xpedeon_agent_monthly_spent{agent_id="1"} 1234.56
xpedeon_agent_monthly_limit{agent_id="1"} 3000.00
xpedeon_agent_monthly_usage_percent{agent_id="1"} 41

xpedeon_cost_events_total 15234
xpedeon_cost_alerts_total{threshold="75"} 342
xpedeon_budget_overrides_active 12
```

### Alerts

```yaml
- name: AgentBudgetExceededDaily
  condition: max(xpedeon_agent_daily_usage_percent) >= 100
  severity: critical
  for: 1m

- name: AgentBudgetWarningMonthly
  condition: max(xpedeon_agent_monthly_usage_percent{threshold="75"}) >= 75
  severity: warning
  for: 5m

- name: ProjectedMonthlyExceeded
  condition: max(xpedeon_agent_projected_monthly_usd) > max(xpedeon_agent_monthly_limit)
  severity: warning
  for: 10m
```

## Best Practices

1. **Initialize Budgets** — Set per-agent limits on creation
2. **Estimate Costs** — Use token heuristics before execution
3. **Record Immediately** — Log costs right after LLM call
4. **Monitor Alerts** — Set up notifications for threshold breaches
5. **Review Overrides** — Audit admin-approved exceptions weekly
6. **Plan Capacity** — Use projected monthly spending for forecasting
7. **Test Limits** — Verify enforcement works before production
8. **Document Reasons** — Always provide reason for overrides (audit)

## References

- PRODUCTION_ROADMAP.md — Phase 2 requirements
- AUDIT_LOGGING_GUIDE.md — Compliance patterns
- Models/CostModels.cs — Data model definitions
- Services/CostTrackingService.cs — Implementation
