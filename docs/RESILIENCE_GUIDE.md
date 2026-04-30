# Resilience Guide

This document describes the resilience and reliability infrastructure for Xpedeon Agent Mission Control, including retry policies, circuit breakers, task checkpointing, and disaster recovery.

## Overview

- **Retry Policies**: Automatic retry with exponential backoff for transient failures
- **Circuit Breaker**: Prevents cascading failures when providers are down
- **Task Checkpointing**: Save progress for resumption after interruption
- **Attempt Tracking**: Full history of execution attempts per task
- **Polly Integration**: Battle-tested resilience library backing the implementation

## Architecture

```
LLMExecutionService.ExecuteAsync()
    ↓
ResilienceService.ExecuteWithResilienceAsync()
    ├─ Check CircuitBreakerState (DB-backed)
    │   ├─ Open → Throw CircuitBreakerOpenException
    │   ├─ HalfOpen → Allow one probe request
    │   └─ Closed → Proceed
    ├─ Load RetryPolicy from DB
    ├─ Execute with retry loop:
    │   ├─ Attempt 1: Execute action
    │   │   ├─ Success → RecordCircuitBreakerSuccess() → return
    │   │   └─ Failure → Log, calculate backoff, wait
    │   ├─ Attempt 2: Retry after backoff
    │   │   └─ ...repeat up to MaxAttempts
    │   └─ All exhausted → RecordCircuitBreakerFailure() → throw
    └─ Save TaskExecutionAttempt for each attempt
```

## Retry Policy

### Configuration

```csharp
public class RetryPolicy {
    int? ProviderId;              // Null = global default
    int MaxAttempts = 3;          // Total retry attempts
    int BaseDelayMs = 1000;       // First retry: 1 second
    int MaxDelayMs = 30000;       // Cap at 30 seconds
    double BackoffMultiplier = 2.0; // Exponential multiplier
    double JitterFactor = 0.1;    // ±10% jitter
    string RetryOnStatusCodes = "429,500,502,503,504"; // Retryable HTTP errors
    bool RetryOnTimeout = true;   // Retry on timeout
    int TimeoutSeconds = 120;     // Per-attempt timeout
}
```

### Exponential Backoff with Jitter

Backoff delays are calculated as:

```
delay = base * multiplier^(attempt-1) + jitter

Attempt 1: 1000 * 2.0^0 = 1000ms ± 10% → 900-1100ms
Attempt 2: 1000 * 2.0^1 = 2000ms ± 10% → 1800-2200ms
Attempt 3: 1000 * 2.0^2 = 4000ms ± 10% → 3600-4400ms
Attempt 4: 1000 * 2.0^3 = 8000ms ± 10% → 7200-8800ms
...capped at MaxDelayMs (30000ms)
```

Jitter prevents thundering herd when many agents retry simultaneously.

### Initialize Retry Policy

```csharp
// Initialize retry policy for a provider
await _resilienceService.InitializeRetryPolicyAsync(
    providerId: 1,
    maxAttempts: 3,
    baseDelayMs: 1000
);

// Custom policy
var policy = new RetryPolicy
{
    ProviderId = 1,
    MaxAttempts = 5,
    BaseDelayMs = 500,
    MaxDelayMs = 60000,
    BackoffMultiplier = 3.0,
    JitterFactor = 0.2,
    RetryOnStatusCodes = "429,503",
    TimeoutSeconds = 90
};
db.RetryPolicies.Add(policy);
```

### Usage

```csharp
var result = await _resilienceService.ExecuteWithResilienceAsync(
    agentId: agent.Id,
    taskId: task.Id,
    providerId: provider.Id,
    action: () => _llmExecution.ExecuteAsync(agent, input, systemPrompt)
);
```

## Circuit Breaker

### State Machine

```
Closed ──[5 consecutive failures]──→ Open
Open ──[60s timeout]──→ HalfOpen
HalfOpen ──[2 successful probes]──→ Closed
HalfOpen ──[any failure]──→ Open
```

### Configuration

```csharp
public class CircuitBreakerState {
    string State = "Closed";         // Closed, Open, HalfOpen
    int ConsecutiveFailures;         // Reset to 0 on success
    int FailureThreshold = 5;        // Failures to open circuit
    int OpenDurationSeconds = 60;    // How long to stay open
    int HalfOpenSuccessThreshold = 2; // Successes to close from HalfOpen
    DateTime? LastOpenedAt;          // When last opened
    DateTime? NextResetAt;           // When to transition to HalfOpen
}
```

### Monitoring Circuit Breaker

```csharp
var status = await _resilienceService.GetProviderStatusAsync(providerId);

Console.WriteLine($"Provider: {status.ProviderName}");
Console.WriteLine($"Circuit State: {status.CircuitState}");
Console.WriteLine($"Consecutive Failures: {status.ConsecutiveFailures}");
Console.WriteLine($"Success Rate (24h): {status.SuccessRateLast24h:P1}");
Console.WriteLine($"Available: {status.IsAvailable}");
```

### Manual Reset

```csharp
// Admin resets circuit breaker (e.g., after provider issue resolved)
await _resilienceService.ResetCircuitBreakerAsync(providerId);
```

### Benefits

- **Prevents cascading failures**: Stops calls to failing provider
- **Self-healing**: Auto-tests after cooldown period
- **Observability**: State tracked in database for monitoring
- **Audit trail**: All state transitions logged to AuditLogs

## Task Checkpointing

### Saving Checkpoints

```csharp
// In LLMExecutionService, after significant milestones
public async Task<LLMResult> ExecuteAsync(Agent agent, string input, string systemPrompt, int taskId)
{
    var checkpoint = await _resilienceService.GetLatestCheckpointAsync(taskId);
    var startIteration = checkpoint?.Iteration ?? 1;

    for (var iteration = startIteration; iteration <= maxToolCalls; iteration++)
    {
        var result = await ExecuteProviderAsync(provider, systemPrompt, currentInput);

        // Save checkpoint after each iteration
        await _resilienceService.SaveCheckpointAsync(
            taskId: taskId,
            label: $"AfterIteration_{iteration}",
            stateJson: JsonSerializer.Serialize(new { currentInput, iteration }),
            iteration: iteration,
            partialOutput: result.Output,
            tokensUsed: result.TotalTokens,
            costUSD: result.CostUSD
        );

        // ...continue with tool calls
    }
}
```

### Resuming from Checkpoint

```csharp
// On task restart
public async Task<AgentTask> ResumeTaskAsync(int taskId)
{
    var canResume = await _resilienceService.CanResumeFromCheckpointAsync(taskId);
    if (!canResume)
        throw new InvalidOperationException("No recovery checkpoint found");

    var checkpoint = await _resilienceService.GetLatestCheckpointAsync(taskId);

    _logger.LogInformation(
        "Resuming task {TaskId} from checkpoint: {Label} (iteration {Iteration})",
        taskId, checkpoint.Label, checkpoint.Iteration
    );

    var state = JsonSerializer.Deserialize<CheckpointState>(checkpoint.StateJson);
    return await ExecuteFromCheckpointAsync(taskId, state);
}
```

### Checkpoint Models

```csharp
public class TaskCheckpoint {
    int Id;
    int TaskId;
    string Label;                 // e.g., "AfterIteration_2"
    string? StateJson;            // Serialized state
    int Iteration;                // Loop iteration count
    string? PartialOutput;        // Accumulated output so far
    int TokensUsed;               // Tokens used to this point
    decimal CostUSD;              // Cost incurred to this point
    bool IsRecoverable;           // Can resume from this point
    DateTime SavedAt;
}
```

## Attempt Tracking

Every execution attempt is recorded:

```csharp
public class TaskExecutionAttempt {
    int Id;
    int TaskId;
    int AgentId;
    int AttemptNumber;            // 1 = first, 2 = first retry, etc.
    DateTime StartedAt;
    DateTime? CompletedAt;
    string Outcome;               // "Success", "Failed", "Timeout", "InProgress"
    string? ErrorMessage;
    int? HttpStatusCode;
    long? DurationMs;
    int TokensUsed;
    decimal CostUSD;
    DateTime? NextRetryAt;        // When next retry is scheduled
}
```

### Query Attempt History

```csharp
// Get all attempts for a task
var attempts = await db.TaskExecutionAttempts
    .Where(a => a.TaskId == taskId)
    .OrderBy(a => a.AttemptNumber)
    .ToListAsync();

foreach (var attempt in attempts)
{
    Console.WriteLine($"Attempt {attempt.AttemptNumber}: {attempt.Outcome}");
    Console.WriteLine($"  Duration: {attempt.DurationMs}ms");
    if (attempt.Outcome == "Failed")
        Console.WriteLine($"  Error: {attempt.ErrorMessage}");
}

// Get success rate for an agent
var agentAttempts = await db.TaskExecutionAttempts
    .Where(a => a.AgentId == agentId && a.StartedAt >= DateTime.UtcNow.AddDays(-7))
    .GroupBy(a => a.Outcome)
    .Select(g => new { Outcome = g.Key, Count = g.Count() })
    .ToListAsync();
```

## Integration Example

Full resilience integration in LLMExecutionService:

```csharp
public class LLMExecutionService
{
    private readonly ResilienceService _resilienceService;

    public async Task<LLMResult> ExecuteAsync(
        Agent agent,
        string userInput,
        string systemPrompt,
        int taskId,
        CancellationToken cancellationToken = default)
    {
        var providerId = agent.LLMProviderId ?? 0;

        return await _resilienceService.ExecuteWithResilienceAsync(
            agentId: agent.Id,
            taskId: taskId,
            providerId: providerId,
            action: async () =>
            {
                var result = await ExecuteProviderAsync(
                    agent.LLMProvider,
                    systemPrompt,
                    userInput,
                    cancellationToken
                );

                // Save checkpoint after successful execution
                await _resilienceService.SaveCheckpointAsync(
                    taskId: taskId,
                    label: "ExecutionComplete",
                    partialOutput: result.Output,
                    tokensUsed: result.TotalTokens,
                    costUSD: result.CostUSD
                );

                return result;
            }
        );
    }
}
```

## Disaster Recovery

### Provider Failure

**Scenario**: LLM provider is returning 5xx errors

1. Circuit breaker opens after 5 failures
2. All new requests fail immediately with `CircuitBreakerOpenException`
3. After 60 seconds, transitions to HalfOpen
4. First successful probe closes the circuit
5. Normal operation resumes

**Manual Recovery**:
```csharp
// If you know the provider is back up
await _resilienceService.ResetCircuitBreakerAsync(providerId);
```

### Application Restart

**Scenario**: Server restart while tasks are running

1. In-progress tasks have checkpoints saved
2. On restart, TaskService queries tasks with Status = "Running"
3. For each, check `CanResumeFromCheckpointAsync()`
4. Resume from last checkpoint (skipping completed iterations)
5. Continue until completion

**Recovery Service**:
```csharp
public class TaskRecoveryService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // On startup, check for interrupted tasks
        await Task.Delay(5000, ct); // Wait for app to start

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var resilienceService = scope.ServiceProvider.GetRequiredService<ResilienceService>();
        var taskService = scope.ServiceProvider.GetRequiredService<TaskService>();

        // Find tasks that were "Running" when app stopped
        var interruptedTasks = await db.AgentTasks
            .Where(t => t.Status == AgentTaskStatus.Running)
            .ToListAsync(ct);

        foreach (var task in interruptedTasks)
        {
            var canResume = await resilienceService.CanResumeFromCheckpointAsync(task.Id);
            if (canResume)
            {
                _logger.LogInformation("Resuming interrupted task {TaskId}", task.Id);
                // Resume from checkpoint
                await taskService.ResumeTaskAsync(task.Id, ct);
            }
            else
            {
                // Mark as failed (cannot resume)
                task.Status = AgentTaskStatus.Failed;
                task.ErrorMessage = "Task interrupted by application restart (no checkpoint)";
                await db.SaveChangesAsync(ct);
            }
        }
    }
}
```

## Monitoring

### Metrics

```prometheus
xpedeon_task_attempts_total{agent_id="1", outcome="Success"} 1523
xpedeon_task_attempts_total{agent_id="1", outcome="Failed"} 42
xpedeon_circuit_breaker_state{provider_id="1"} 0  # 0=Closed, 1=Open, 2=HalfOpen
xpedeon_circuit_breaker_failures{provider_id="1"} 3
xpedeon_retry_attempts_total{provider_id="1"} 145
xpedeon_task_checkpoints_total 23456
```

### Alerts

```yaml
- name: CircuitBreakerOpen
  condition: xpedeon_circuit_breaker_state == 1
  severity: critical
  for: 1m
  action: Check provider status, consider manual reset

- name: HighRetryRate
  condition: rate(xpedeon_retry_attempts_total[5m]) > 5
  severity: warning
  for: 5m
  action: Investigate provider reliability

- name: TaskSuccessRateDrop
  condition: rate(xpedeon_task_attempts_total{outcome="Success"}[5m]) / rate(xpedeon_task_attempts_total[5m]) < 0.9
  severity: warning
  for: 5m
```

## Testing

### Unit Test: Retry Logic

```csharp
[Fact]
public async Task ExecuteWithResilienceAsync_RetriesOnTransientFailure()
{
    var callCount = 0;

    await _resilienceService.InitializeRetryPolicyAsync(
        providerId: 1,
        maxAttempts: 3,
        baseDelayMs: 100  // Fast for tests
    );

    // Fail first 2 times, succeed on 3rd
    var result = await _resilienceService.ExecuteWithResilienceAsync(
        agentId: 1,
        taskId: 1,
        providerId: 1,
        action: async () =>
        {
            callCount++;
            if (callCount < 3)
                throw new HttpRequestException("503 Service Unavailable");
            return "success";
        }
    );

    Assert.Equal("success", result);
    Assert.Equal(3, callCount);

    // Verify attempts were recorded
    var attempts = await db.TaskExecutionAttempts
        .Where(a => a.TaskId == 1)
        .OrderBy(a => a.AttemptNumber)
        .ToListAsync();

    Assert.Equal(3, attempts.Count);
    Assert.All(attempts.Take(2), a => Assert.Equal("Failed", a.Outcome));
    Assert.Equal("Success", attempts.Last().Outcome);
}
```

### Unit Test: Circuit Breaker

```csharp
[Fact]
public async Task ExecuteWithResilienceAsync_OpensCircuitAfterThreshold()
{
    var state = new CircuitBreakerState
    {
        ProviderId = 1,
        State = "Closed",
        FailureThreshold = 5
    };
    await db.CircuitBreakerStates.AddAsync(state);

    // Fail 5 times to trip the circuit
    for (int i = 0; i < 5; i++)
    {
        try
        {
            await _resilienceService.ExecuteWithResilienceAsync(
                1, i, 1, () => throw new Exception("Provider down")
            );
        }
        catch { }
    }

    // Verify circuit is open
    var dbState = await db.CircuitBreakerStates.FirstAsync(s => s.ProviderId == 1);
    Assert.Equal("Open", dbState.State);

    // Next call should fail immediately with CircuitBreakerOpenException
    await Assert.ThrowsAsync<CircuitBreakerOpenException>(
        () => _resilienceService.ExecuteWithResilienceAsync(1, 100, 1, () => Task.FromResult("ok"))
    );
}
```

## Best Practices

1. **Tune Retry Limits** — Set MaxAttempts appropriate to provider SLAs
2. **Use Jitter** — Always add jitter to avoid thundering herd
3. **Set Timeout** — TimeoutSeconds prevents hanging requests
4. **Monitor Circuit Breaker** — Alert when circuit opens
5. **Checkpoint Wisely** — Save checkpoints at meaningful milestones
6. **Test Recovery** — Simulate failures to verify retry and recovery paths
7. **Log Attempts** — All attempts tracked for debugging and analytics
8. **Coordinate with Budget** — Retries incur cost; ensure budget accounts for retries

## References

- PRODUCTION_ROADMAP.md — Phase 3 requirements
- Models/ResilienceModels.cs — Data models
- Services/ResilienceService.cs — Implementation
- COST_TRACKING_GUIDE.md — Budget considerations for retries
