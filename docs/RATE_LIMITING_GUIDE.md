# Rate Limiting Guide

This document describes the rate limiting system for Xpedeon Agent Mission Control, including throttling strategies, configuration, queueing, and monitoring.

## Overview

- **Purpose**: Prevent agents from overwhelming LLM providers or downstream services
- **Scope**: Per-agent rate limits (calls, tokens, concurrency)
- **Enforcement**: Middleware intercepts requests before execution
- **Strategies**: Block, Queue, or Degrade (configurable per agent)
- **Queuing**: Automatic task queueing when limits exceeded (optional)

## Architecture

```
HTTP Request
    ↓
RateLimitingMiddleware
    ├─ Extract agent ID from request
    ├─ Call RateLimitingService.CheckRateLimitAsync()
    │   ├─ Check calls/minute limit
    │   ├─ Check calls/hour limit
    │   ├─ Check calls/day limit
    │   ├─ Check tokens/hour limit
    │   ├─ Check tokens/day limit
    │   └─ Check concurrent tasks limit
    ├─ If limits exceeded:
    │   ├─ "Block" → Return 429 TooManyRequests
    │   ├─ "Queue" → Queue task, return 429 with queue info
    │   └─ "Degrade" → Allow but log, add X-Rate-Limit-Degraded header
    └─ If allowed → Continue to next middleware
```

## Rate Limit Models

### RateLimitPolicy

Per-agent rate limit configuration:

```csharp
public class RateLimitPolicy {
    int Id;                           // Primary key
    int AgentId;                      // FK to Agent
    int? CallsPerMinute;              // API calls/minute limit (null = unlimited)
    int? CallsPerHour;                // API calls/hour limit (null = unlimited)
    int? CallsPerDay;                 // API calls/day limit (null = unlimited)
    int? TokensPerHour;               // Tokens/hour limit (null = unlimited)
    int? TokensPerDay;                // Tokens/day limit (null = unlimited)
    int MaxConcurrentTasks;           // Max simultaneous tasks (default: 5)
    string LimitExceededAction;       // "Block", "Queue", or "Degrade"
    DateTime CreatedAt;
    DateTime UpdatedAt;
}
```

### RateLimitEvent

Records limit checks and violations:

```csharp
public class RateLimitEvent {
    int Id;
    int AgentId;
    string LimitType;                 // "CallPerMinute", "TokenPerDay", etc.
    int CurrentValue;                 // Current usage
    int LimitValue;                   // Configured limit
    string Action;                    // "Allowed" or "Blocked"
    string? BlockReason;              // Why it was blocked
    DateTime OccurredAt;
    string? RequestPath;              // API endpoint
    string? ClientIpAddress;          // Client making request
}
```

### RateLimitedTaskQueue

Tracks tasks queued due to rate limits:

```csharp
public class RateLimitedTaskQueue {
    int Id;
    int AgentId;
    int TaskId;
    string QueueReason;               // Which limit was exceeded
    int QueuePosition;                // Position in queue
    DateTime QueuedAt;
    DateTime? EstimatedExecuteAt;     // When task will likely execute
    DateTime? ExecutedAt;             // When task actually ran
    bool WasSkipped;                  // Whether task was abandoned
}
```

## Configuration

### Initialize Rate Limit Policy

```csharp
public class AgentService
{
    private readonly RateLimitingService _rateLimitingService;

    public async Task<Agent> CreateAgentAsync(CreateAgentRequest request)
    {
        var agent = new Agent { Name = request.Name, ... };
        await _agentRepository.SaveAsync(agent);

        // Initialize rate limit policy for the agent
        await _rateLimitingService.InitializePolicyAsync(
            agentId: agent.Id,
            callsPerMinute: 60,         // 60 calls/minute (1 per second)
            callsPerHour: 3600,         // 3600 calls/hour
            callsPerDay: 10000,         // 10k calls/day
            tokensPerHour: 100000,      // 100k tokens/hour
            tokensPerDay: 500000,       // 500k tokens/day
            maxConcurrentTasks: 5,      // 5 parallel tasks max
            limitExceededAction: "Queue" // Queue tasks instead of blocking
        );

        return agent;
    }
}
```

### Common Policy Templates

```csharp
// Conservative: For testing/development
{
    CallsPerMinute: 10,
    CallsPerHour: 100,
    CallsPerDay: 1000,
    TokensPerHour: 10000,
    TokensPerDay: 50000,
    MaxConcurrentTasks: 2,
    LimitExceededAction: "Block"
}

// Standard: For production agents
{
    CallsPerMinute: 60,
    CallsPerHour: 3600,
    CallsPerDay: 10000,
    TokensPerHour: 100000,
    TokensPerDay: 500000,
    MaxConcurrentTasks: 5,
    LimitExceededAction: "Queue"
}

// Aggressive: For high-throughput agents
{
    CallsPerMinute: 300,
    CallsPerHour: 10000,
    CallsPerDay: 100000,
    TokensPerHour: 1000000,
    TokensPerDay: 10000000,
    MaxConcurrentTasks: 20,
    LimitExceededAction: "Degrade"
}

// Unlimited: No rate limiting
{
    CallsPerMinute: null,
    CallsPerHour: null,
    CallsPerDay: null,
    TokensPerHour: null,
    TokensPerDay: null,
    MaxConcurrentTasks: 100,
    LimitExceededAction: "Allow"
}
```

## Limit Types

### Call-Based Limits

Count number of API calls:

- **CallsPerMinute** — Limits peak request rate
- **CallsPerHour** — Prevents hourly spikes
- **CallsPerDay** — Prevents daily quota abuse

Use for: Preventing rate limit violations from upstream LLM providers.

### Token-Based Limits

Count tokens (input + output):

- **TokensPerHour** — Limits hourly token consumption
- **TokensPerDay** — Limits daily token spend

Use for: Controlling cost and preventing token exhaustion.

### Concurrency Limits

Limit parallel task execution:

- **MaxConcurrentTasks** — Max tasks running simultaneously

Use for: Preventing resource exhaustion and controlling memory/CPU.

## Enforcement Strategies

### Block Strategy

Immediately reject request when limit exceeded:

```csharp
// Policy with "Block" action
limitExceededAction = "Block"

// Result: HTTP 429 TooManyRequests
// {
//   "error": "Rate limit exceeded",
//   "reason": "CallsPerMinute",
//   "message": "Too many requests. Try again later."
// }
```

Best for: Strict enforcement, cost control, development.

### Queue Strategy

Queue tasks when limit exceeded:

```csharp
// Policy with "Queue" action
limitExceededAction = "Queue"

// Result: HTTP 429 TooManyRequests with queue info
// {
//   "error": "Rate limit exceeded",
//   "reason": "CallsPerMinute",
//   "message": "Your task has been queued",
//   "queuePosition": 3,
//   "estimatedExecuteAt": "2026-04-30T14:35:00Z",
//   "retryAfter": 90
// }

// Background worker dequeues and executes when limits allow
await _rateLimitingService.DequeueAndExecuteAsync(agentId);
```

Best for: Batch processing, resilient systems, SLA fulfillment.

### Degrade Strategy

Allow execution but log as degraded:

```csharp
// Policy with "Degrade" action
limitExceededAction = "Degrade"

// Result: HTTP 200 OK (request succeeds)
// But response includes:
// X-Rate-Limit-Degraded: true
// X-Rate-Limit-Warning: TokensPerDay (95% used)
```

Best for: High-priority tasks, graceful degradation, best-effort services.

## Usage Examples

### Check Before Executing Task

```csharp
public class LLMExecutionService
{
    private readonly RateLimitingService _rateLimitingService;

    public async Task<string> ExecuteAsync(Agent agent, string input)
    {
        // Estimate tokens for this request
        var estimatedTokens = EstimateTokens(input);

        // Check rate limit
        var checkResult = await _rateLimitingService.CheckRateLimitAsync(
            agentId: agent.Id,
            estimatedTokens: estimatedTokens
        );

        if (!checkResult.IsAllowed && !checkResult.IsDegraded)
            throw new RateLimitExceededException(checkResult.Reason);

        // Execute (may be degraded)
        var response = await CallLLMAsync(agent, input);
        return response.Output;
    }
}
```

### Get Rate Limit Statistics

```csharp
var stats = await _rateLimitingService.GetStatisticsAsync(agentId: 42);

Console.WriteLine($"Calls/min: {stats.TotalCallsLastMinute}/60");
Console.WriteLine($"Calls/hour: {stats.TotalCallsLastHour}/3600");
Console.WriteLine($"Tokens/hour: {stats.TotalTokensLastHour}/100000");
Console.WriteLine($"Concurrent: {stats.CurrentConcurrentTasks}/5");
Console.WriteLine($"Queued: {stats.QueuedTasksCount}");
Console.WriteLine($"Violations/day: {stats.LimitViolationsLastDay}");
```

### Dequeue Tasks Periodically

```csharp
// Hosted service that processes queued tasks
public class RateLimitQueueProcessorService : BackgroundService
{
    private readonly RateLimitingService _rateLimitingService;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Every 30 seconds, try to dequeue and execute
            var agentIds = await GetAgentIdsWithQueuedTasksAsync();
            
            foreach (var agentId in agentIds)
            {
                var dequeued = await _rateLimitingService.DequeueAndExecuteAsync(agentId);
                if (dequeued)
                    _logger.LogInformation("Dequeued and executing task for agent {AgentId}", agentId);
            }

            await Task.Delay(30000, stoppingToken);
        }
    }
}
```

## Middleware Integration

### Extract Agent ID

Middleware looks for agent ID in this order:

1. **Header** — `X-Agent-Id: 42`
2. **Query** — `?agentId=42`
3. **Route** — `/api/agents/42/execute`

```csharp
// Middleware will use the first matching source
GET /api/agents/42/tasks
X-Agent-Id: 99
// Uses agent ID 42 (from route)

GET /api/agents/tasks?agentId=50
// Uses agent ID 50 (from query)

GET /api/agents/60/tasks
X-Agent-Id: 99
// Uses agent ID 60 (from route, which takes precedence)
```

### Exempt Paths

These paths bypass rate limiting:

- `/health` — Health checks
- `/health/detailed` — Detailed health
- `/.well-known` — Well-known endpoints
- `/swagger` — API documentation
- `/metrics` — Prometheus metrics
- `/api/auth` — Authentication
- `/hubs/agent` — SignalR hub

## Monitoring

### Metrics

```prometheus
xpedeon_rate_limit_checks_total{agent_id="1"} 1523
xpedeon_rate_limit_blocks_total{agent_id="1"} 42
xpedeon_rate_limit_queued_total{agent_id="1"} 15
xpedeon_rate_limit_current_calls_per_minute{agent_id="1"} 45
xpedeon_rate_limit_current_tokens_per_hour{agent_id="1"} 45000
xpedeon_rate_limit_concurrent_tasks{agent_id="1"} 3
xpedeon_rate_limit_queued_tasks{agent_id="1"} 5
```

### Alerts

```yaml
- name: RateLimitViolationSpike
  condition: rate(xpedeon_rate_limit_blocks_total[5m]) > 5
  severity: warning
  for: 5m

- name: QueueBacklog
  condition: xpedeon_rate_limit_queued_tasks > 100
  severity: warning
  for: 10m

- name: ConcurrentTaskLimit
  condition: xpedeon_rate_limit_concurrent_tasks > 4 # nearMaxOf5
  severity: info
  for: 5m
```

## Testing

### Unit Test: Rate Limit Enforcement

```csharp
[Fact]
public async Task CheckRateLimitAsync_BlocksWhenCallLimitExceeded()
{
    var agentId = 1;

    // Initialize with 5 calls/minute limit
    await _rateLimitingService.InitializePolicyAsync(
        agentId: agentId,
        callsPerMinute: 5,
        limitExceededAction: "Block"
    );

    // Make 5 calls
    for (int i = 0; i < 5; i++)
    {
        var result = await _rateLimitingService.CheckRateLimitAsync(agentId);
        Assert.True(result.IsAllowed);
    }

    // 6th call should be blocked
    var blockedResult = await _rateLimitingService.CheckRateLimitAsync(agentId);
    Assert.False(blockedResult.IsAllowed);
    Assert.Equal("CallsPerMinute", blockedResult.Reason);
}
```

### Integration Test: Queueing

```csharp
[Fact]
public async Task CheckRateLimitAsync_QueuesWhenQueueActionSet()
{
    var agentId = 1;
    var taskId = 100;

    await _rateLimitingService.InitializePolicyAsync(
        agentId: agentId,
        callsPerMinute: 5,
        limitExceededAction: "Queue"
    );

    // Max out calls
    for (int i = 0; i < 5; i++)
        await _rateLimitingService.CheckRateLimitAsync(agentId);

    // Next call should queue
    var result = await _rateLimitingService.CheckRateLimitAsync(agentId);
    Assert.False(result.IsAllowed);
    Assert.True(result.ShouldQueue);

    // Queue the task
    await _rateLimitingService.QueueTaskAsync(agentId, taskId, "CallsPerMinute");

    // Verify queue entry exists
    using (var db = _dbContextFactory.CreateDbContext())
    {
        var queueEntry = await db.RateLimitedTaskQueues
            .FirstAsync(q => q.TaskId == taskId);
        Assert.Equal(agentId, queueEntry.AgentId);
        Assert.Equal("CallsPerMinute", queueEntry.QueueReason);
        Assert.Null(queueEntry.ExecutedAt);
    }
}
```

## Best Practices

1. **Set Realistic Limits** — Match agent workload and provider quotas
2. **Use Queue Strategy** — Better UX than blocking for batch tasks
3. **Monitor Violations** — Alert on unusual patterns
4. **Test Limits** — Verify limits work in staging before production
5. **Gradual Rollout** — Start conservative, increase as needed
6. **Document Reasons** — Note why specific limits were chosen
7. **Adjust Periodically** — Review and adjust based on usage patterns
8. **Combine with Cost** — Use cost limits + rate limits together

## Troubleshooting

### Tasks Keep Getting Blocked

**Problem:** Tasks are being rejected even though they should be allowed.

**Solutions:**
1. Check `RateLimitPolicy` has correct `LimitExceededAction` (should be "Queue" for batch tasks)
2. Verify limit values aren't too low
3. Check if other agents are sharing limits
4. Use `GetStatisticsAsync()` to see actual usage

### Queue Never Processes

**Problem:** Queued tasks aren't executing.

**Solutions:**
1. Ensure `RateLimitQueueProcessorService` is registered and running
2. Check logs for exceptions in dequeue process
3. Verify limits have reset (time-based limits window passed)
4. Check task status (may be marked as skipped or failed)

### High Violation Rate

**Problem:** Rate limit violations spike suddenly.

**Solutions:**
1. Check for load test or unusual traffic
2. Verify LLM provider quota wasn't reduced
3. Check if other agents are sharing the same policy
4. Consider temporarily increasing limits while investigating

## References

- PRODUCTION_ROADMAP.md — Phase 2 requirements
- COST_TRACKING_GUIDE.md — Spend limits coordination
- Models/RateLimitingModels.cs — Data model definitions
- Services/RateLimitingService.cs — Implementation
- Middleware/RateLimitingMiddleware.cs — Middleware implementation
