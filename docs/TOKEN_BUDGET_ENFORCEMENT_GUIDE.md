# Token Budget Enforcement Guide

This document describes the token budget enforcement system that integrates cost tracking and rate limiting to prevent runaway spending and resource exhaustion.

## Overview

- **Purpose**: Enforce spending and rate limit checks before LLM execution
- **Scope**: Per-task pre-execution validation and post-execution cost recording
- **Integration**: Bridges CostTrackingService and RateLimitingService with LLMExecutionService
- **Enforcement**: Blocks, queues, or degrades execution based on budget/limit status

## Architecture

```
Task Execution Request
    ↓
TokenBudgetEnforcementService.CheckAndEnforceAsync()
    ├─ Estimate tokens from user input
    ├─ Estimate cost from tokens
    ├─ Call CostTrackingService.CheckBudgetAsync()
    │   └─ Return: Budget OK or Exceeded
    ├─ Call RateLimitingService.CheckRateLimitAsync()
    │   └─ Return: Allowed, Queue, or Degrade
    └─ Return decision: Allow, Block, or Queue
        ↓
    [Allow] → Execute LLMExecutionService.ExecuteAsync()
        ↓
    TokenBudgetEnforcementService.RecordExecutionAsync()
        ├─ Record actual cost via CostTrackingService
        ├─ Log to AuditService
        └─ Update budget and rate limit tracking
```

## Enforcement Points

### Pre-Execution Check

Before any LLM call:

```csharp
// In TaskService or similar
var checkResult = await _tokenBudgetEnforcementService.CheckAndEnforceAsync(
    agentId: agent.Id,
    taskId: task.Id,
    userInput: request.Input,
    provider: agent.LLMProvider
);

if (!checkResult.IsAllowed)
{
    if (checkResult.ShouldQueue)
    {
        // Queue the task for later execution
        await _rateLimitingService.QueueTaskAsync(
            agent.Id,
            task.Id,
            checkResult.BlockReason
        );
        return "Task queued due to " + checkResult.BlockReason;
    }
    else
    {
        throw new BudgetExceededException(checkResult.BlockReason);
    }
}

// Safe to execute
var result = await _llmExecutionService.ExecuteAsync(agent, request.Input, agent.SystemPrompt);
```

### Post-Execution Recording

After LLM execution:

```csharp
// Record actual execution cost
var recorded = await _tokenBudgetEnforcementService.RecordExecutionAsync(
    agentId: agent.Id,
    taskId: task.Id,
    result: result,
    provider: agent.LLMProvider
);

if (!recorded)
    _logger.LogWarning("Failed to record execution cost for task {TaskId}", task.Id);
```

## Token Estimation

### Heuristic-Based Estimation

Before execution, tokens are estimated using:

```csharp
// Rough heuristic: ~4 characters per token
// Plus 20% overhead for system prompt and formatting
var estimatedTokens = (userInput.Length / 4.0) * 1.2;

// Example:
// Input: "What is 2+2?" (13 chars)
// Estimated: (13 / 4) * 1.2 ≈ 4 tokens
```

### Cost Estimation

Cost is estimated assuming 50/50 input/output split:

```csharp
// Estimated tokens split equally between input/output
var inputTokens = estimatedTokens / 2;
var outputTokens = estimatedTokens / 2;

// Cost calculation based on provider pricing
var inputCost = (inputTokens / 1000m) * provider.CostPer1kInputTokens;
var outputCost = (outputTokens / 1000m) * provider.CostPer1kOutputTokens;
var totalEstimatedCost = inputCost + outputCost;

// Example: Claude 3 Sonnet with 100 estimated tokens
// Input: (50 / 1000) * $0.003 = $0.00015
// Output: (50 / 1000) * $0.015 = $0.00075
// Total: $0.0009
```

### Actual Cost Recording

After execution, actual cost is recorded:

```csharp
// LLMResult contains actual token counts and cost
var result = await _llmExecutionService.ExecuteAsync(...);

// result.PromptTokens = 45 (actual)
// result.CompletionTokens = 52 (actual)
// result.TotalTokens = 97 (actual)
// result.CostUSD = 0.00089 (actual)

// Record the ACTUAL values, not estimates
await _tokenBudgetEnforcementService.RecordExecutionAsync(...);
```

## Spending Forecast

Get current spending forecast including budget and rate limit status:

```csharp
var forecast = await _tokenBudgetEnforcementService.GetSpendingForecastAsync(agentId: 42);

if (forecast != null)
{
    Console.WriteLine($"Daily: ${forecast.CurrentDailySpentUSD} / ${forecast.DailyLimitUSD}");
    Console.WriteLine($"Monthly: ${forecast.CurrentMonthlySpentUSD} / ${forecast.MonthlyLimitUSD}");
    Console.WriteLine($"Tokens/hour: {forecast.CurrentTokensPerHour} / {forecast.TokensPerHourLimit}");
    Console.WriteLine($"Concurrent: {forecast.CurrentConcurrentTasks} / {forecast.MaxConcurrentTasks}");
    Console.WriteLine($"Queued: {forecast.QueuedTasksCount}");
    Console.WriteLine($"Status: {forecast.StatusMessage}");
    Console.WriteLine($"Can Execute: {forecast.CanExecuteMore}");
}
```

## Integration with LLMExecutionService

### Pattern 1: Direct Integration

Modify LLMExecutionService to inject TokenBudgetEnforcementService:

```csharp
public class LLMExecutionService
{
    private readonly TokenBudgetEnforcementService _budgetEnforcement;

    public async Task<LLMResult> ExecuteAsync(
        Agent agent,
        string userInput,
        string systemPrompt,
        int taskId,
        CancellationToken cancellationToken = default)
    {
        // Check before execution
        var checkResult = await _budgetEnforcement.CheckAndEnforceAsync(
            agent.Id, taskId, userInput, agent.LLMProvider
        );

        if (!checkResult.IsAllowed)
            throw new BudgetExceededException(checkResult.BlockReason);

        // Execute normally
        var result = await ExecuteInternalAsync(agent, userInput, systemPrompt, cancellationToken);

        // Record after execution
        await _budgetEnforcement.RecordExecutionAsync(agent.Id, taskId, result, agent.LLMProvider);

        return result;
    }
}
```

### Pattern 2: Wrapper Service

Create a wrapper that enforces limits:

```csharp
public class EnforcedLLMExecutionService
{
    private readonly LLMExecutionService _llmExecution;
    private readonly TokenBudgetEnforcementService _budgetEnforcement;

    public async Task<LLMResult> ExecuteWithEnforcementAsync(
        Agent agent,
        string userInput,
        string systemPrompt,
        int taskId)
    {
        var checkResult = await _budgetEnforcement.CheckAndEnforceAsync(
            agent.Id, taskId, userInput, agent.LLMProvider
        );

        if (!checkResult.IsAllowed)
        {
            if (checkResult.ShouldQueue)
                throw new ShouldQueueException(checkResult.BlockReason);
            else
                throw new BudgetExceededException(checkResult.BlockReason);
        }

        var result = await _llmExecution.ExecuteAsync(agent, userInput, systemPrompt);
        await _budgetEnforcement.RecordExecutionAsync(agent.Id, taskId, result, agent.LLMProvider);
        return result;
    }
}
```

### Pattern 3: TaskService Integration

TaskService already orchestrates execution:

```csharp
public class TaskService
{
    private readonly TokenBudgetEnforcementService _budgetEnforcement;
    private readonly LLMExecutionService _llmExecution;

    public async Task<AgentTask> ExecuteTaskAsync(int taskId)
    {
        var task = await _db.Tasks.FindAsync(taskId);
        var agent = task.Agent;

        // Pre-execution check
        var checkResult = await _budgetEnforcement.CheckAndEnforceAsync(
            agent.Id, task.Id, task.Input, agent.LLMProvider
        );

        if (!checkResult.IsAllowed)
        {
            task.Status = checkResult.ShouldQueue ? "Queued" : "Failed";
            task.ErrorMessage = checkResult.BlockReason;
            await _db.SaveChangesAsync();
            return task;
        }

        // Execute
        try
        {
            var result = await _llmExecution.ExecuteAsync(
                agent,
                task.Input,
                agent.SystemPrompt,
                taskId.ToString()
            );

            // Post-execution record
            await _budgetEnforcement.RecordExecutionAsync(agent.Id, task.Id, result, agent.LLMProvider);

            task.Output = result.Output;
            task.TokensUsed = result.TotalTokens;
            task.Cost = result.CostUSD;
            task.Status = "Completed";
        }
        catch (Exception ex)
        {
            task.Status = "Failed";
            task.ErrorMessage = ex.Message;
        }

        await _db.SaveChangesAsync();
        return task;
    }
}
```

## Handling Queueing

When tasks are queued due to rate limits:

```csharp
public class RateLimitQueueProcessorService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var budgetEnforcement = scope.ServiceProvider.GetRequiredService<TokenBudgetEnforcementService>();
                var rateLimiting = scope.ServiceProvider.GetRequiredService<RateLimitingService>();

                // Get queued tasks
                var queuedTasks = await db.RateLimitedTaskQueues
                    .Where(q => q.ExecutedAt == null && !q.WasSkipped)
                    .OrderBy(q => q.QueuePosition)
                    .Take(10)
                    .ToListAsync();

                foreach (var queueEntry in queuedasks)
                {
                    var task = await db.AgentTasks.FindAsync(queueEntry.TaskId);
                    if (task == null) continue;

                    var agent = await db.Agents.FindAsync(queueEntry.AgentId);
                    if (agent == null) continue;

                    // Check if limits allow now
                    var forecastResult = await budgetEnforcement.GetSpendingForecastAsync(agent.Id);
                    if (forecastResult?.CanExecuteMore != true)
                    {
                        _logger.LogInformation("Task {TaskId} still cannot execute, skipping for now", task.Id);
                        continue;
                    }

                    // Dequeue and execute
                    await rateLimiting.DequeueAndExecuteAsync(agent.Id);

                    _logger.LogInformation("Dequeued task {TaskId} for agent {AgentId}", task.Id, agent.Id);
                }
            }

            await Task.Delay(30000, stoppingToken); // Check every 30 seconds
        }
    }
}
```

## Monitoring and Alerts

### Key Metrics

```prometheus
xpedeon_agent_can_execute{agent_id="1"} 1        # 1 = can execute, 0 = cannot
xpedeon_agent_estimated_tokens{agent_id="1"} 250 # Tokens for pending request
xpedeon_agent_forecast_daily_usage{agent_id="1"} 65 # Daily budget usage %
xpedeon_agent_forecast_monthly_usage{agent_id="1"} 42 # Monthly budget usage %
```

### Alerts

```yaml
- name: AgentCannotExecute
  condition: xpedeon_agent_can_execute == 0
  severity: warning
  for: 10m
  action: Check budget and rate limit status

- name: AgentCostNearDailyLimit
  condition: xpedeon_agent_forecast_daily_usage >= 90
  severity: warning
  for: 5m

- name: AgentCostNearMonthlyLimit
  condition: xpedeon_agent_forecast_monthly_usage >= 80
  severity: warning
  for: 10m
```

## Testing

### Unit Test: Pre-Execution Check

```csharp
[Fact]
public async Task CheckAndEnforceAsync_BlocksWhenBudgetExceeded()
{
    var agentId = 1;
    var provider = new LLMProvider
    {
        CostPer1kInputTokens = 0.003m,
        CostPer1kOutputTokens = 0.015m
    };

    // Initialize with low budget
    await _costTrackingService.InitializeBudgetAsync(agentId, dailyLimitUSD: 0.01m);

    // Try to execute with high input (will estimate high cost)
    var result = await _budgetEnforcementService.CheckAndEnforceAsync(
        agentId,
        taskId: 1,
        userInput: new string('x', 10000), // Large input
        provider: provider
    );

    Assert.False(result.IsAllowed);
    Assert.Equal("Cost budget exceeded", result.BlockReason);
}
```

### Integration Test: Recording

```csharp
[Fact]
public async Task RecordExecutionAsync_UpdatesBudgetAndLogs()
{
    var agentId = 1;
    var taskId = 100;

    await _costTrackingService.InitializeBudgetAsync(agentId, null, 1000m);

    var result = new LLMResult
    {
        Output = "Hello",
        PromptTokens = 100,
        CompletionTokens = 50,
        TotalTokens = 150,
        CostUSD = 0.0015m,
        ModelUsed = "claude-3-sonnet"
    };

    var recorded = await _budgetEnforcementService.RecordExecutionAsync(
        agentId, taskId, result, _provider
    );

    Assert.True(recorded);

    // Verify budget was updated
    using (var db = _dbFactory.CreateDbContext())
    {
        var budget = await db.AgentBudgets.FirstAsync(b => b.AgentId == agentId);
        Assert.Equal(0.0015m, budget.MonthSpentUSD);
    }

    // Verify cost event was logged
    var costEvents = await _db.CostEvents
        .Where(e => e.TaskId == taskId)
        .ToListAsync();
    Assert.Single(costEvents);
    Assert.Equal(150, costEvents[0].TokensUsed);
}
```

## Best Practices

1. **Check Before Execute** — Always call CheckAndEnforceAsync before LLM execution
2. **Record After Execute** — Always call RecordExecutionAsync with actual results
3. **Handle Queueing** — Implement background service to process queued tasks
4. **Monitor Forecast** — Check spending forecast in dashboards
5. **Test Limits** — Verify enforcement works with various budget levels
6. **Log Decisions** — All enforcement decisions are logged to AuditLogs
7. **Gradual Limits** — Start conservative, increase based on actual usage
8. **Forecast Planning** — Use ProjectedMonthlyUSD to anticipate month-end budget

## Troubleshooting

### Tasks Always Blocked

**Check:**
1. Is budget initialized? (null budget = unlimited)
2. Is daily limit too low? (Compare to estimated costs)
3. Are rate limit policies active and restrictive?
4. Check audit logs for specific block reasons

### Forecast Shows Cannot Execute

**Check:**
1. Daily or monthly budget exceeded → Increase limits or wait for reset
2. Rate limit violated → Check calls/tokens/concurrency usage
3. Queued tasks backlog → Check queue processor service is running
4. Provider not configured → Verify LLM provider is set

## References

- COST_TRACKING_GUIDE.md — Budget models and enforcement
- RATE_LIMITING_GUIDE.md — Rate limit policies and strategies
- PRODUCTION_ROADMAP.md — Phase 2 requirements
- Services/TokenBudgetEnforcementService.cs — Implementation
- Services/CostTrackingService.cs — Cost tracking
- Services/RateLimitingService.cs — Rate limiting
