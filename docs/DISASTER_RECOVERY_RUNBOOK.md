# Disaster Recovery Runbook

This runbook provides procedures for recovering from common failure scenarios in Xpedeon Agent Mission Control.

## Quick Reference

| Scenario | Severity | Recovery Time | Auto-Recovery |
|----------|----------|---------------|---------------|
| LLM Provider Down | High | 1-60 min | ✅ (circuit breaker) |
| Database Connection Lost | Critical | 5-30 min | ❌ (manual) |
| Application Crash | High | 1-5 min | ✅ (restart + task recovery) |
| Budget Exceeded | Medium | 5-15 min | ❌ (admin approval) |
| Rate Limit Hit | Low | 0-60 min | ✅ (auto-reset) |
| Circuit Breaker Open | Medium | 1-5 min | ✅ (auto-half-open) |

## Scenario 1: LLM Provider Unavailable

**Symptoms:**
- Tasks failing with 5xx errors
- Circuit breaker state = "Open"
- Alert: `CircuitBreakerOpen`

**Auto-Recovery:**
1. Circuit breaker opens after 5 consecutive failures
2. No new requests sent to failing provider
3. After 60 seconds, circuit transitions to HalfOpen
4. First successful probe closes circuit

**Manual Recovery:**
```csharp
// Check provider status
var status = await _resilienceService.GetProviderStatusAsync(providerId);
Console.WriteLine($"State: {status.CircuitState}");
Console.WriteLine($"Failures: {status.ConsecutiveFailures}");

// If provider is back up, reset circuit breaker
await _resilienceService.ResetCircuitBreakerAsync(providerId);
```

**Steps:**
1. Check provider status page (Anthropic, OpenAI, etc.)
2. If provider is confirmed down, wait for recovery
3. If provider is up but circuit still open, manually reset
4. Monitor success rate after reset
5. Update IMPLEMENTATION_STATUS.md with incident notes

---

## Scenario 2: Database Connection Lost

**Symptoms:**
- Application errors starting with "Database" or "Cannot open connection"
- Health check endpoint returning "Unhealthy" for DB component
- No tasks executing

**Investigation:**
```bash
# Check health endpoint
curl http://localhost:5220/health

# Check database connectivity
# If SQLite: verify xpedeon.db file is not locked
# If SQL Server: verify connection string and server availability
```

**Recovery Steps:**

For SQLite:
1. Check if another process holds a lock on `xpedeon.db`
2. Restart the application to release any orphaned connections
3. If file is corrupted, restore from backup

For SQL Server:
1. Verify SQL Server service is running
2. Test connection string manually
3. Check SQL Server error log for specific issues
4. Verify network connectivity to DB server

**Restart Procedure:**
```bash
# Restart application
systemctl restart xpedeon-agent-mission-control

# Verify health
curl http://localhost:5220/health
```

---

## Scenario 3: Application Crash

**Symptoms:**
- Application process not running
- Tasks stuck in "Running" status
- Health endpoint unreachable

**Auto-Recovery on Restart:**
```
[Startup] Apply EF Core migrations
[Startup] Encrypt unencrypted secrets
[Startup] BackgroundHealthCheckService starts (30s intervals)
[Startup] BackgroundAlertProcessorService starts (30s intervals)
[Startup] AgentSchedulerService starts (1 min intervals)
```

**Task Recovery:**
Tasks in "Running" state when app crashed need investigation:
```csharp
// Find interrupted tasks
var interruptedTasks = await db.AgentTasks
    .Where(t => t.Status == AgentTaskStatus.Running)
    .ToListAsync();

// Check for checkpoints
foreach (var task in interruptedTasks)
{
    var canResume = await _resilienceService.CanResumeFromCheckpointAsync(task.Id);
    if (canResume)
    {
        var checkpoint = await _resilienceService.GetLatestCheckpointAsync(task.Id);
        Console.WriteLine($"Task {task.Id}: resume from {checkpoint.Label} (iteration {checkpoint.Iteration})");
        // TODO: Resume task from checkpoint
    }
    else
    {
        Console.WriteLine($"Task {task.Id}: mark as failed");
        task.Status = AgentTaskStatus.Failed;
        task.ErrorMessage = "Task interrupted by application restart";
    }
}
await db.SaveChangesAsync();
```

---

## Scenario 4: Budget Exceeded

**Symptoms:**
- Tasks failing with "Budget exceeded" error
- CostAlert with ThresholdPercent = 100
- Notification sent to configured email/Slack

**Investigation:**
```csharp
var summary = await _costTrackingService.GetCostSummaryAsync(agentId);
Console.WriteLine($"Daily: ${summary.TodaySpentUSD} / ${summary.DailyLimitUSD}");
Console.WriteLine($"Monthly: ${summary.MonthSpentUSD} / ${summary.MonthlyLimitUSD}");
```

**Recovery Options:**

Option A: Wait for budget reset
- Daily budget resets at UTC midnight
- Monthly budget resets 1st of month

Option B: Admin override
```csharp
await _costTrackingService.ApplyOverrideAsync(
    agentId: agentId,
    overrideAmountUSD: 50.00m,  // Extra $50 allowed
    period: "Daily",
    reason: "Emergency production fix",
    approvedBy: "admin@company.com",
    ticketRef: "INC-12345",
    expiresAt: DateTime.UtcNow.AddHours(4)  // 4-hour override
);
```

Option C: Increase budget limits
```csharp
var budget = await db.AgentBudgets.FirstOrDefaultAsync(b => b.AgentId == agentId);
budget.DailyCostLimitUSD = budget.DailyCostLimitUSD + 100m; // Increase by $100
await db.SaveChangesAsync();
```

---

## Scenario 5: Rate Limit Exceeded

**Symptoms:**
- Tasks getting 429 responses
- RateLimitEvents showing blocked requests
- Queue backlog growing

**Investigation:**
```csharp
var stats = await _rateLimitingService.GetStatisticsAsync(agentId);
Console.WriteLine($"Calls/min: {stats.TotalCallsLastMinute}");
Console.WriteLine($"Violations: {stats.LimitViolationsLastHour}");
Console.WriteLine($"Queued: {stats.QueuedTasksCount}");
```

**Recovery:**
1. Wait for rate limit window to reset (1 minute for calls/minute)
2. Queued tasks will execute automatically when limits allow
3. If permanent relief needed, increase limits in RateLimitPolicy

```csharp
var policy = await db.RateLimitPolicies.FirstOrDefaultAsync(p => p.AgentId == agentId);
policy.CallsPerMinute = policy.CallsPerMinute * 2; // Double the limit
await db.SaveChangesAsync();
```

---

## Scenario 6: High Failure Rate

**Symptoms:**
- Task success rate dropping
- Multiple retry attempts logged
- Alert: `HighRetryRate`

**Investigation:**
```csharp
var recentAttempts = await db.TaskExecutionAttempts
    .Where(a => a.AgentId == agentId && a.StartedAt >= DateTime.UtcNow.AddHours(-1))
    .GroupBy(a => a.Outcome)
    .Select(g => new { Outcome = g.Key, Count = g.Count() })
    .ToListAsync();

// Look for patterns in errors
var failedAttempts = await db.TaskExecutionAttempts
    .Where(a => a.Outcome == "Failed" && a.StartedAt >= DateTime.UtcNow.AddHours(-1))
    .ToListAsync();

var errorGroups = failedAttempts
    .GroupBy(a => a.ErrorMessage)
    .OrderByDescending(g => g.Count())
    .ToList();
```

**Recovery:**
1. Identify the common error pattern
2. Fix the root cause (provider config, prompt, input validation)
3. Clear retry queue if needed
4. Reset circuit breaker if provider was identified as cause

---

## Backup & Restore

### SQLite Database

**Backup:**
```bash
# Copy SQLite database file
cp xpedeon.db xpedeon.db.backup.$(date +%Y%m%d-%H%M%S)
```

**Restore:**
```bash
# Stop application
systemctl stop xpedeon-agent-mission-control

# Restore from backup
cp xpedeon.db.backup.20260430-143000 xpedeon.db

# Start application
systemctl start xpedeon-agent-mission-control
```

### SQL Server Database

**Backup:**
```sql
BACKUP DATABASE XpedeonAgentMissionControl
TO DISK = '/backups/xpedeon-20260430.bak'
WITH COMPRESSION;
```

**Restore:**
```sql
RESTORE DATABASE XpedeonAgentMissionControl
FROM DISK = '/backups/xpedeon-20260430.bak'
WITH REPLACE;
```

---

## Alert Acknowledgment

After handling an incident, acknowledge open alerts:

```csharp
await _alertService.AcknowledgeAlertAsync(
    costAlertId: alertId,
    acknowledgedBy: "ops@company.com",
    actionTaken: "Increased daily budget from $100 to $200",
    notes: "High-priority feature request caused spike"
);
```

---

## Post-Incident Actions

1. **Immediately**: Stabilize the system, resume operations
2. **Within 1 hour**: Document incident in issue tracker
3. **Within 24 hours**: Root cause analysis
4. **Within 1 week**: Implement preventive measures
5. **Monthly**: Review all incidents, update thresholds and limits

---

## Runbook Maintenance

This runbook should be updated when:
- New failure modes are discovered
- Recovery procedures change
- Alert thresholds are adjusted
- New services are added

**Last Updated**: 2026-04-30
**Owner**: Platform Engineering
