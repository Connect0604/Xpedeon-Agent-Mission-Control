using System.Text;
using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class ObservabilityService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly AuditService _auditService;
    private readonly ILogger<ObservabilityService> _logger;

    public ObservabilityService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        AuditService auditService,
        ILogger<ObservabilityService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<AgentMetricsSnapshot> CollectMetricsAsync(int agentId, int windowHours = 24)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync();

        var windowStart = DateTime.UtcNow.AddHours(-windowHours);

        var recentTasks = await db.Tasks
            .Where(t => t.AgentId == agentId && t.CreatedAt >= windowStart)
            .ToListAsync();

        var completedTasks = recentTasks.Where(t => t.Status == AgentTaskStatus.Completed).ToList();
        var failedTasks = recentTasks.Where(t => t.Status == AgentTaskStatus.Failed).ToList();
        var runningTasks = recentTasks.Where(t => t.Status == AgentTaskStatus.Running).ToList();

        var totalDone = completedTasks.Count + failedTasks.Count;
        var successRate = totalDone > 0 ? (double)completedTasks.Count / totalDone * 100.0 : 100.0;

        // Compute completion times for completed tasks
        var completionTimesMs = completedTasks
            .Where(t => t.CompletedAt.HasValue)
            .Select(t => (t.CompletedAt!.Value - t.CreatedAt).TotalMilliseconds)
            .OrderBy(ms => ms)
            .ToList();

        var avgMs = completionTimesMs.Any() ? completionTimesMs.Average() : 0;
        var p95Ms = completionTimesMs.Any()
            ? completionTimesMs[(int)(completionTimesMs.Count * 0.95)]
            : 0;

        var totalTokens = recentTasks.Sum(t => (long)t.TokensUsed);
        var totalCost = recentTasks.Sum(t => t.CostUSD);
        var avgCost = completedTasks.Any() ? totalCost / completedTasks.Count : 0;

        // Budget usage
        var budget = await db.AgentBudgets.FirstOrDefaultAsync(b => b.AgentId == agentId);
        var dailyBudgetPercent = budget?.DailyUsagePercent ?? 0;

        // Check SLA
        var sla = await db.AgentSLAs.FirstOrDefaultAsync(s => s.AgentId == agentId && s.IsActive);
        var isSLAMet = true;
        if (sla != null)
        {
            isSLAMet = successRate >= sla.TargetSuccessRatePercent &&
                       (p95Ms == 0 || p95Ms <= sla.MaxCompletionSeconds * 1000);
        }

        var snapshot = new AgentMetricsSnapshot
        {
            AgentId = agentId,
            TasksCompleted = completedTasks.Count,
            TasksFailed = failedTasks.Count,
            TasksRunning = runningTasks.Count,
            SuccessRatePercent = successRate,
            AvgCompletionMs = avgMs,
            P95CompletionMs = p95Ms,
            TotalTokensConsumed = totalTokens,
            TotalCostUSD = totalCost,
            AvgCostPerTask = avgCost,
            DailyBudgetUsagePercent = dailyBudgetPercent,
            IsSLAMet = isSLAMet,
            SnapshotAt = DateTime.UtcNow
        };

        db.AgentMetricsSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        return snapshot;
    }

    public async Task CheckSLAAsync(int agentId)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync();

        var sla = await db.AgentSLAs.FirstOrDefaultAsync(s => s.AgentId == agentId && s.IsActive);
        if (sla == null) return;

        var windowStart = DateTime.UtcNow.AddHours(-sla.MeasurementWindowHours);

        var recentTasks = await db.Tasks
            .Where(t => t.AgentId == agentId && t.CreatedAt >= windowStart)
            .ToListAsync();

        var completedTasks = recentTasks.Where(t => t.Status == AgentTaskStatus.Completed).ToList();
        var failedTasks = recentTasks.Where(t => t.Status == AgentTaskStatus.Failed).ToList();
        var totalDone = completedTasks.Count + failedTasks.Count;

        if (totalDone == 0) return; // No tasks to evaluate

        var successRate = (double)completedTasks.Count / totalDone * 100.0;

        // Check success rate
        if (successRate < sla.TargetSuccessRatePercent)
        {
            await CreateViolationAsync(
                agentId,
                ViolationType: "SuccessRate",
                targetValue: sla.TargetSuccessRatePercent,
                actualValue: successRate,
                severity: successRate < sla.TargetSuccessRatePercent - 10 ? "Breach" : "Warning",
                description: $"Success rate {successRate:F1}% is below target {sla.TargetSuccessRatePercent}%"
            );
        }

        // Check P95 completion time
        var completionTimesMs = completedTasks
            .Where(t => t.CompletedAt.HasValue)
            .Select(t => (t.CompletedAt!.Value - t.CreatedAt).TotalMilliseconds)
            .OrderBy(ms => ms)
            .ToList();

        if (completionTimesMs.Any())
        {
            var p95Ms = completionTimesMs[(int)(completionTimesMs.Count * 0.95)];
            var maxAllowedMs = sla.MaxCompletionSeconds * 1000;

            if (p95Ms > maxAllowedMs)
            {
                await CreateViolationAsync(
                    agentId,
                    ViolationType: "CompletionTime",
                    targetValue: maxAllowedMs,
                    actualValue: p95Ms,
                    severity: p95Ms > maxAllowedMs * 2 ? "Breach" : "Warning",
                    description: $"P95 completion {p95Ms:F0}ms exceeds max {maxAllowedMs}ms"
                );
            }
        }

        // Check consecutive failures
        var recentByTime = recentTasks
            .OrderByDescending(t => t.CreatedAt)
            .Take(sla.MaxConsecutiveFailures + 1)
            .ToList();

        var consecutiveFails = 0;
        foreach (var t in recentByTime)
        {
            if (t.Status == AgentTaskStatus.Failed)
                consecutiveFails++;
            else
                break;
        }

        if (consecutiveFails >= sla.MaxConsecutiveFailures)
        {
            await CreateViolationAsync(
                agentId,
                ViolationType: "ConsecutiveFailures",
                targetValue: sla.MaxConsecutiveFailures,
                actualValue: consecutiveFails,
                severity: "Breach",
                description: $"{consecutiveFails} consecutive failures exceeds max {sla.MaxConsecutiveFailures}"
            );
        }
    }

    public async Task<string> GetPrometheusMetricsAsync()
    {
        using var db = await _dbContextFactory.CreateDbContextAsync();

        var sb = new StringBuilder();

        // --- System-wide metrics ---
        sb.AppendLine("# HELP xpedeon_agents_total Total number of agents");
        sb.AppendLine("# TYPE xpedeon_agents_total gauge");
        sb.AppendLine($"xpedeon_agents_total {await db.Agents.CountAsync()}");

        var activeAgents = await db.Agents.CountAsync(a => a.Status == AgentStatus.Active);
        sb.AppendLine("# HELP xpedeon_agents_active Currently active agents");
        sb.AppendLine("# TYPE xpedeon_agents_active gauge");
        sb.AppendLine($"xpedeon_agents_active {activeAgents}");

        var pendingTasks = await db.Tasks.CountAsync(t => t.Status == AgentTaskStatus.Pending);
        var runningTasks = await db.Tasks.CountAsync(t => t.Status == AgentTaskStatus.Running);
        var completedToday = await db.Tasks.CountAsync(t =>
            t.Status == AgentTaskStatus.Completed &&
            t.CreatedAt >= DateTime.UtcNow.Date);
        var failedToday = await db.Tasks.CountAsync(t =>
            t.Status == AgentTaskStatus.Failed &&
            t.CreatedAt >= DateTime.UtcNow.Date);

        sb.AppendLine("# HELP xpedeon_tasks_pending Tasks waiting to execute");
        sb.AppendLine("# TYPE xpedeon_tasks_pending gauge");
        sb.AppendLine($"xpedeon_tasks_pending {pendingTasks}");

        sb.AppendLine("# HELP xpedeon_tasks_running Tasks currently executing");
        sb.AppendLine("# TYPE xpedeon_tasks_running gauge");
        sb.AppendLine($"xpedeon_tasks_running {runningTasks}");

        sb.AppendLine("# HELP xpedeon_tasks_completed_today Tasks completed today");
        sb.AppendLine("# TYPE xpedeon_tasks_completed_today counter");
        sb.AppendLine($"xpedeon_tasks_completed_today {completedToday}");

        sb.AppendLine("# HELP xpedeon_tasks_failed_today Tasks failed today");
        sb.AppendLine("# TYPE xpedeon_tasks_failed_today counter");
        sb.AppendLine($"xpedeon_tasks_failed_today {failedToday}");

        // --- Per-agent metrics ---
        var latestSnapshots = await db.AgentMetricsSnapshots
            .GroupBy(s => s.AgentId)
            .Select(g => g.OrderByDescending(s => s.SnapshotAt).First())
            .ToListAsync();

        sb.AppendLine("# HELP xpedeon_agent_success_rate Success rate % (0-100) per agent");
        sb.AppendLine("# TYPE xpedeon_agent_success_rate gauge");
        foreach (var snap in latestSnapshots)
            sb.AppendLine($"xpedeon_agent_success_rate{{agent_id=\"{snap.AgentId}\"}} {snap.SuccessRatePercent:F1}");

        sb.AppendLine("# HELP xpedeon_agent_p95_completion_ms P95 completion time in ms per agent");
        sb.AppendLine("# TYPE xpedeon_agent_p95_completion_ms gauge");
        foreach (var snap in latestSnapshots)
            sb.AppendLine($"xpedeon_agent_p95_completion_ms{{agent_id=\"{snap.AgentId}\"}} {snap.P95CompletionMs:F0}");

        sb.AppendLine("# HELP xpedeon_agent_daily_budget_usage Daily budget usage percent per agent");
        sb.AppendLine("# TYPE xpedeon_agent_daily_budget_usage gauge");
        foreach (var snap in latestSnapshots)
            sb.AppendLine($"xpedeon_agent_daily_budget_usage{{agent_id=\"{snap.AgentId}\"}} {snap.DailyBudgetUsagePercent}");

        sb.AppendLine("# HELP xpedeon_agent_sla_met Whether SLA is currently met (1=met, 0=violated) per agent");
        sb.AppendLine("# TYPE xpedeon_agent_sla_met gauge");
        foreach (var snap in latestSnapshots)
            sb.AppendLine($"xpedeon_agent_sla_met{{agent_id=\"{snap.AgentId}\"}} {(snap.IsSLAMet ? 1 : 0)}");

        // --- Circuit breaker metrics ---
        var circuitBreakers = await db.CircuitBreakerStates
            .Include(c => c.Provider)
            .ToListAsync();

        sb.AppendLine("# HELP xpedeon_circuit_breaker_state Circuit breaker state (0=Closed, 1=Open, 2=HalfOpen) per provider");
        sb.AppendLine("# TYPE xpedeon_circuit_breaker_state gauge");
        foreach (var cb in circuitBreakers)
        {
            var stateValue = cb.State switch { "Open" => 1, "HalfOpen" => 2, _ => 0 };
            sb.AppendLine($"xpedeon_circuit_breaker_state{{provider_id=\"{cb.ProviderId}\",provider=\"{cb.Provider?.Name ?? "unknown"}\"}} {stateValue}");
        }

        // --- Cost metrics ---
        var budgets = await db.AgentBudgets.ToListAsync();
        sb.AppendLine("# HELP xpedeon_agent_daily_spent Daily USD spent per agent");
        sb.AppendLine("# TYPE xpedeon_agent_daily_spent gauge");
        foreach (var b in budgets)
            sb.AppendLine($"xpedeon_agent_daily_spent{{agent_id=\"{b.AgentId}\"}} {b.TodaySpentUSD:F4}");

        sb.AppendLine("# HELP xpedeon_agent_monthly_spent Monthly USD spent per agent");
        sb.AppendLine("# TYPE xpedeon_agent_monthly_spent gauge");
        foreach (var b in budgets)
            sb.AppendLine($"xpedeon_agent_monthly_spent{{agent_id=\"{b.AgentId}\"}} {b.MonthSpentUSD:F4}");

        // --- SLA violations ---
        var recentViolations = await db.SLAViolations
            .Where(v => !v.IsAcknowledged && v.OccurredAt >= DateTime.UtcNow.AddDays(-1))
            .GroupBy(v => new { v.AgentId, v.ViolationType })
            .Select(g => new { g.Key.AgentId, g.Key.ViolationType, Count = g.Count() })
            .ToListAsync();

        sb.AppendLine("# HELP xpedeon_sla_violations_total SLA violations in last 24h per agent and type");
        sb.AppendLine("# TYPE xpedeon_sla_violations_total counter");
        foreach (var v in recentViolations)
            sb.AppendLine($"xpedeon_sla_violations_total{{agent_id=\"{v.AgentId}\",type=\"{v.ViolationType}\"}} {v.Count}");

        return sb.ToString();
    }

    public async Task InitializeSLAAsync(
        int agentId,
        int targetCompletionSeconds = 30,
        int maxCompletionSeconds = 120,
        double targetSuccessRatePercent = 95.0)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync();

        var existing = await db.AgentSLAs.FirstOrDefaultAsync(s => s.AgentId == agentId);
        if (existing != null) return;

        var sla = new AgentSLA
        {
            AgentId = agentId,
            TargetCompletionSeconds = targetCompletionSeconds,
            MaxCompletionSeconds = maxCompletionSeconds,
            TargetSuccessRatePercent = targetSuccessRatePercent,
            TargetAvailabilityPercent = 99.0,
            MaxConsecutiveFailures = 3,
            MeasurementWindowHours = 24,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        db.AgentSLAs.Add(sla);
        await db.SaveChangesAsync();
    }

    private async Task CreateViolationAsync(
        int agentId,
        string ViolationType,
        double targetValue,
        double actualValue,
        string severity,
        string description,
        int? taskId = null)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync();

        // Avoid duplicate violation in the last hour
        var recentViolation = await db.SLAViolations
            .FirstOrDefaultAsync(v =>
                v.AgentId == agentId &&
                v.ViolationType == ViolationType &&
                v.OccurredAt >= DateTime.UtcNow.AddHours(-1));

        if (recentViolation != null)
            return;

        var violation = new SLAViolation
        {
            AgentId = agentId,
            TaskId = taskId,
            ViolationType = ViolationType,
            TargetValue = targetValue,
            ActualValue = actualValue,
            Severity = severity,
            Description = description,
            OccurredAt = DateTime.UtcNow,
            IsAcknowledged = false
        };

        db.SLAViolations.Add(violation);
        await db.SaveChangesAsync();

        _logger.LogWarning(
            "SLA {Severity} for agent {AgentId}: {Description}",
            severity, agentId, description);

        await _auditService.LogAsync(
            action: "SLAViolation",
            entityType: "SLAViolation",
            entityId: violation.Id.ToString(),
            entityName: $"Agent #{agentId}",
            afterSnapshot: violation,
            changeDescription: $"SLA {severity}: {description}"
        );
    }
}
