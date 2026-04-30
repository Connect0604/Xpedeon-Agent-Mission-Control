using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class RateLimitingService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly AuditService _auditService;
    private readonly ILogger<RateLimitingService> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public RateLimitingService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        AuditService auditService,
        ILogger<RateLimitingService> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _dbContextFactory = dbContextFactory;
        _auditService = auditService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<RateLimitCheckResult> CheckRateLimitAsync(int agentId, int estimatedTokens = 0)
    {
        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var policy = await db.RateLimitPolicies.FirstOrDefaultAsync(p => p.AgentId == agentId);
            if (policy == null)
                return new RateLimitCheckResult { IsAllowed = true }; // No policy = unlimited

            // Check all limits
            var checkResults = new List<(string limitType, bool exceeded, int current, int limit)>();

            // Check calls per minute
            if (policy.CallsPerMinute.HasValue)
            {
                var callsLastMinute = await CountCallsInTimeWindowAsync(db, agentId, TimeSpan.FromMinutes(1));
                if (callsLastMinute >= policy.CallsPerMinute.Value)
                    checkResults.Add(("CallsPerMinute", true, callsLastMinute, policy.CallsPerMinute.Value));
            }

            // Check calls per hour
            if (policy.CallsPerHour.HasValue)
            {
                var callsLastHour = await CountCallsInTimeWindowAsync(db, agentId, TimeSpan.FromHours(1));
                if (callsLastHour >= policy.CallsPerHour.Value)
                    checkResults.Add(("CallsPerHour", true, callsLastHour, policy.CallsPerHour.Value));
            }

            // Check calls per day
            if (policy.CallsPerDay.HasValue)
            {
                var callsLastDay = await CountCallsInTimeWindowAsync(db, agentId, TimeSpan.FromDays(1));
                if (callsLastDay >= policy.CallsPerDay.Value)
                    checkResults.Add(("CallsPerDay", true, callsLastDay, policy.CallsPerDay.Value));
            }

            // Check tokens per hour
            if (policy.TokensPerHour.HasValue && estimatedTokens > 0)
            {
                var tokensLastHour = await CountTokensInTimeWindowAsync(db, agentId, TimeSpan.FromHours(1));
                if (tokensLastHour + estimatedTokens > policy.TokensPerHour.Value)
                    checkResults.Add(("TokensPerHour", true, tokensLastHour + estimatedTokens, policy.TokensPerHour.Value));
            }

            // Check tokens per day
            if (policy.TokensPerDay.HasValue && estimatedTokens > 0)
            {
                var tokensLastDay = await CountTokensInTimeWindowAsync(db, agentId, TimeSpan.FromDays(1));
                if (tokensLastDay + estimatedTokens > policy.TokensPerDay.Value)
                    checkResults.Add(("TokensPerDay", true, tokensLastDay + estimatedTokens, policy.TokensPerDay.Value));
            }

            // Check concurrent tasks
            var currentConcurrentTasks = await CountConcurrentTasksAsync(db, agentId);
            if (currentConcurrentTasks >= policy.MaxConcurrentTasks)
                checkResults.Add(("ConcurrentTasks", true, currentConcurrentTasks, policy.MaxConcurrentTasks));

            // If any limit exceeded, handle based on policy
            if (checkResults.Count > 0)
            {
                var clientIp = _httpContextAccessor?.HttpContext?.Connection?.RemoteIpAddress?.ToString();
                var requestPath = _httpContextAccessor?.HttpContext?.Request?.Path.Value;

                foreach (var (limitType, _, current, limit) in checkResults)
                {
                    // Log the event
                    var limitEvent = new RateLimitEvent
                    {
                        AgentId = agentId,
                        LimitType = limitType,
                        CurrentValue = current,
                        LimitValue = limit,
                        Action = policy.LimitExceededAction,
                        BlockReason = $"{limitType} exceeded ({current}/{limit})",
                        OccurredAt = DateTime.UtcNow,
                        RequestPath = requestPath,
                        ClientIpAddress = clientIp
                    };

                    db.RateLimitEvents.Add(limitEvent);
                }

                await db.SaveChangesAsync();

                _logger.LogWarning(
                    "Rate limit exceeded for agent {AgentId}: {LimitCount} limits exceeded. Action: {Action}",
                    agentId, checkResults.Count, policy.LimitExceededAction);

                if (policy.LimitExceededAction == "Block")
                    return new RateLimitCheckResult { IsAllowed = false, Reason = checkResults[0].limitType };

                if (policy.LimitExceededAction == "Queue")
                    return new RateLimitCheckResult { IsAllowed = false, ShouldQueue = true, Reason = checkResults[0].limitType };

                // Degrade = allow but log
                return new RateLimitCheckResult { IsAllowed = true, IsDegraded = true };
            }

            return new RateLimitCheckResult { IsAllowed = true };
        }
    }

    public async Task<bool> QueueTaskAsync(int agentId, int taskId, string reason)
    {
        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var queuePosition = await db.RateLimitedTaskQueues
                .Where(q => q.AgentId == agentId && q.ExecutedAt == null)
                .MaxAsync(q => (int?)q.QueuePosition) ?? 0;

            var queueEntry = new RateLimitedTaskQueue
            {
                AgentId = agentId,
                TaskId = taskId,
                QueueReason = reason,
                QueuePosition = queuePosition + 1,
                QueuedAt = DateTime.UtcNow,
                EstimatedExecuteAt = EstimateExecutionTime(queuePosition + 1)
            };

            db.RateLimitedTaskQueues.Add(queueEntry);
            await db.SaveChangesAsync();

            await _auditService.LogAsync(
                action: "QueueTask",
                entityType: "RateLimitedTaskQueue",
                entityId: queueEntry.Id.ToString(),
                entityName: $"Task #{taskId}",
                afterSnapshot: queueEntry,
                changeDescription: $"Task queued due to {reason}"
            );

            _logger.LogInformation(
                "Task {TaskId} queued for agent {AgentId} due to {Reason}. Position: {Position}",
                taskId, agentId, reason, queueEntry.QueuePosition);

            return true;
        }
    }

    public async Task<bool> DequeueAndExecuteAsync(int agentId)
    {
        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var nextTask = await db.RateLimitedTaskQueues
                .Where(q => q.AgentId == agentId && q.ExecutedAt == null && !q.WasSkipped)
                .OrderBy(q => q.QueuePosition)
                .FirstOrDefaultAsync();

            if (nextTask == null)
                return false;

            nextTask.ExecutedAt = DateTime.UtcNow;
            db.RateLimitedTaskQueues.Update(nextTask);
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "Task {TaskId} dequeued and executing for agent {AgentId}",
                nextTask.TaskId, agentId);

            return true;
        }
    }

    public async Task<RateLimitStatistics?> GetStatisticsAsync(int agentId)
    {
        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var callsLastMinute = await CountCallsInTimeWindowAsync(db, agentId, TimeSpan.FromMinutes(1));
            var callsLastHour = await CountCallsInTimeWindowAsync(db, agentId, TimeSpan.FromHours(1));
            var callsLastDay = await CountCallsInTimeWindowAsync(db, agentId, TimeSpan.FromDays(1));
            var tokensLastHour = await CountTokensInTimeWindowAsync(db, agentId, TimeSpan.FromHours(1));
            var tokensLastDay = await CountTokensInTimeWindowAsync(db, agentId, TimeSpan.FromDays(1));
            var currentConcurrent = await CountConcurrentTasksAsync(db, agentId);
            var queuedTasks = await db.RateLimitedTaskQueues
                .CountAsync(q => q.AgentId == agentId && q.ExecutedAt == null && !q.WasSkipped);
            var violationsLastHour = await db.RateLimitEvents
                .CountAsync(e => e.AgentId == agentId && e.Action == "Blocked" && e.OccurredAt >= DateTime.UtcNow.AddHours(-1));
            var violationsLastDay = await db.RateLimitEvents
                .CountAsync(e => e.AgentId == agentId && e.Action == "Blocked" && e.OccurredAt >= DateTime.UtcNow.AddDays(-1));

            return new RateLimitStatistics
            {
                AgentId = agentId,
                TotalCallsLastMinute = callsLastMinute,
                TotalCallsLastHour = callsLastHour,
                TotalCallsLastDay = callsLastDay,
                TotalTokensLastHour = tokensLastHour,
                TotalTokensLastDay = tokensLastDay,
                CurrentConcurrentTasks = currentConcurrent,
                QueuedTasksCount = queuedTasks,
                LimitViolationsLastHour = violationsLastHour,
                LimitViolationsLastDay = violationsLastDay,
                AsOfDate = DateTime.UtcNow
            };
        }
    }

    public async Task<bool> InitializePolicyAsync(
        int agentId,
        int? callsPerMinute = null,
        int? callsPerHour = null,
        int? callsPerDay = null,
        int? tokensPerHour = null,
        int? tokensPerDay = null,
        int maxConcurrentTasks = 5,
        string limitExceededAction = "Block")
    {
        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var existingPolicy = await db.RateLimitPolicies.FirstOrDefaultAsync(p => p.AgentId == agentId);
            if (existingPolicy != null)
                return false; // Policy already exists

            var policy = new RateLimitPolicy
            {
                AgentId = agentId,
                CallsPerMinute = callsPerMinute,
                CallsPerHour = callsPerHour,
                CallsPerDay = callsPerDay,
                TokensPerHour = tokensPerHour,
                TokensPerDay = tokensPerDay,
                MaxConcurrentTasks = maxConcurrentTasks,
                LimitExceededAction = limitExceededAction,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            db.RateLimitPolicies.Add(policy);
            await db.SaveChangesAsync();

            await _auditService.LogAsync(
                action: "InitializePolicy",
                entityType: "RateLimitPolicy",
                entityId: policy.Id.ToString(),
                entityName: $"Agent #{agentId}",
                afterSnapshot: policy,
                changeDescription: $"Rate limit policy initialized"
            );

            return true;
        }
    }

    private async Task<int> CountCallsInTimeWindowAsync(AppDbContext db, int agentId, TimeSpan window)
    {
        var cutoff = DateTime.UtcNow.Subtract(window);
        return await db.RateLimitEvents
            .Where(e => e.AgentId == agentId && e.OccurredAt >= cutoff)
            .CountAsync();
    }

    private async Task<int> CountTokensInTimeWindowAsync(AppDbContext db, int agentId, TimeSpan window)
    {
        var cutoff = DateTime.UtcNow.Subtract(window);
        return await db.CostEvents
            .Where(e => e.AgentId == agentId && e.OccurredAt >= cutoff)
            .SumAsync(e => (int?)e.TokensUsed) ?? 0;
    }

    private async Task<int> CountConcurrentTasksAsync(AppDbContext db, int agentId)
    {
        return await db.Tasks
            .CountAsync(t => t.AgentId == agentId && (t.Status == "Running" || t.Status == "Pending"));
    }

    private static DateTime EstimateExecutionTime(int queuePosition)
    {
        // Rough estimate: 30 seconds per queued task
        return DateTime.UtcNow.AddSeconds(queuePosition * 30);
    }
}

public class RateLimitCheckResult
{
    public bool IsAllowed { get; set; } = true;
    public bool ShouldQueue { get; set; }
    public bool IsDegraded { get; set; }
    public string? Reason { get; set; }
}
