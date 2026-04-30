using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class TokenBudgetEnforcementService
{
    private readonly CostTrackingService _costTrackingService;
    private readonly RateLimitingService _rateLimitingService;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly AuditService _auditService;
    private readonly ILogger<TokenBudgetEnforcementService> _logger;

    public TokenBudgetEnforcementService(
        CostTrackingService costTrackingService,
        RateLimitingService rateLimitingService,
        IDbContextFactory<AppDbContext> dbContextFactory,
        AuditService auditService,
        ILogger<TokenBudgetEnforcementService> logger)
    {
        _costTrackingService = costTrackingService;
        _rateLimitingService = rateLimitingService;
        _dbContextFactory = dbContextFactory;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<TokenBudgetCheckResult> CheckAndEnforceAsync(
        int agentId,
        int taskId,
        string userInput,
        LLMProvider provider)
    {
        try
        {
            // Estimate tokens for this request (rough heuristic)
            var estimatedTokens = EstimateTokens(userInput);

            // Check cost budget
            var budgetOk = await _costTrackingService.CheckBudgetAsync(agentId, EstimateCost(provider, estimatedTokens));
            if (!budgetOk)
            {
                _logger.LogWarning(
                    "Cost budget exceeded for agent {AgentId}. Task {TaskId} cannot execute.",
                    agentId, taskId);

                return new TokenBudgetCheckResult
                {
                    IsAllowed = false,
                    BlockReason = "Cost budget exceeded",
                    ShouldQueue = false
                };
            }

            // Check rate limits
            var rateLimitResult = await _rateLimitingService.CheckRateLimitAsync(agentId, estimatedTokens);
            if (!rateLimitResult.IsAllowed)
            {
                _logger.LogWarning(
                    "Rate limit exceeded for agent {AgentId}: {Reason}. Task {TaskId} action: {Action}",
                    agentId, rateLimitResult.Reason, taskId, rateLimitResult.ShouldQueue ? "Queue" : "Block");

                return new TokenBudgetCheckResult
                {
                    IsAllowed = false,
                    BlockReason = rateLimitResult.Reason,
                    ShouldQueue = rateLimitResult.ShouldQueue,
                    Degraded = rateLimitResult.IsDegraded
                };
            }

            return new TokenBudgetCheckResult
            {
                IsAllowed = true,
                EstimatedTokens = estimatedTokens,
                EstimatedCostUSD = EstimateCost(provider, estimatedTokens)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking token budget enforcement for agent {AgentId}", agentId);
            // On error, allow execution but log it
            return new TokenBudgetCheckResult { IsAllowed = true };
        }
    }

    public async Task<bool> RecordExecutionAsync(
        int agentId,
        int taskId,
        LLMResult result,
        LLMProvider provider)
    {
        try
        {
            // Record the cost event
            await _costTrackingService.RecordCostAsync(
                agentId: agentId,
                taskId: taskId,
                costUSD: result.CostUSD,
                tokensUsed: result.TotalTokens,
                inputTokens: result.PromptTokens,
                outputTokens: result.CompletionTokens,
                providerName: provider.Name,
                modelName: provider.ModelName
            );

            // Log to audit trail
            await _auditService.LogAsync(
                action: "ExecuteTask",
                entityType: "AgentTask",
                entityId: taskId.ToString(),
                entityName: $"Task #{taskId}",
                afterSnapshot: new
                {
                    TaskId = taskId,
                    AgentId = agentId,
                    Tokens = result.TotalTokens,
                    Cost = result.CostUSD,
                    Model = result.ModelUsed
                },
                changeDescription: $"Executed with {result.TotalTokens} tokens, ${result.CostUSD:F4} cost ({result.ModelUsed})"
            );

            _logger.LogInformation(
                "Recorded execution for agent {AgentId}, task {TaskId}: {Tokens} tokens, ${Cost:F4}",
                agentId, taskId, result.TotalTokens, result.CostUSD);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error recording execution cost for agent {AgentId}, task {TaskId}",
                agentId, taskId);
            return false;
        }
    }

    public async Task<TokenSpendingForecast?> GetSpendingForecastAsync(int agentId)
    {
        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var budget = await db.AgentBudgets.FirstOrDefaultAsync(b => b.AgentId == agentId);
            if (budget == null)
                return null;

            var rateLimit = await db.RateLimitPolicies.FirstOrDefaultAsync(p => p.AgentId == agentId);

            var costSummary = await _costTrackingService.GetCostSummaryAsync(agentId);
            var rateLimitStats = await _rateLimitingService.GetStatisticsAsync(agentId);

            return new TokenSpendingForecast
            {
                AgentId = agentId,
                CurrentDailySpentUSD = costSummary?.TodaySpentUSD ?? 0,
                DailyLimitUSD = costSummary?.DailyLimitUSD ?? 0,
                DailyRemainingUSD = budget?.DailyRemainingUSD ?? decimal.MaxValue,
                DailyUsagePercent = costSummary?.DailyUsagePercent ?? 0,

                CurrentMonthlySpentUSD = costSummary?.MonthSpentUSD ?? 0,
                MonthlyLimitUSD = costSummary?.MonthlyLimitUSD ?? 0,
                MonthlyRemainingUSD = budget?.MonthlyRemainingUSD ?? decimal.MaxValue,
                MonthlyUsagePercent = costSummary?.MonthlyUsagePercent ?? 0,
                ProjectedMonthlyUSD = costSummary?.ProjectedMonthlyUSD ?? 0,

                CurrentCallsPerMinute = rateLimitStats?.TotalCallsLastMinute ?? 0,
                CallsPerMinuteLimit = rateLimit?.CallsPerMinute ?? 0,

                CurrentTokensPerHour = rateLimitStats?.TotalTokensLastHour ?? 0,
                TokensPerHourLimit = rateLimit?.TokensPerHour ?? 0,

                CurrentConcurrentTasks = rateLimitStats?.CurrentConcurrentTasks ?? 0,
                MaxConcurrentTasks = rateLimit?.MaxConcurrentTasks ?? 0,

                QueuedTasksCount = rateLimitStats?.QueuedTasksCount ?? 0,

                CanExecuteMore = (budget?.HasDailyLimit != true || budget?.DailyRemainingUSD > 0) &&
                                 (budget?.HasMonthlyLimit != true || budget?.MonthlyRemainingUSD > 0),

                StatusMessage = GetStatusMessage(budget, costSummary, rateLimit, rateLimitStats),

                AsOfDate = DateTime.UtcNow
            };
        }
    }

    private static string GetStatusMessage(
        AgentBudget? budget,
        CostSummary? costSummary,
        RateLimitPolicy? rateLimit,
        RateLimitStatistics? rateLimitStats)
    {
        if (budget?.IsDailyLimitExceeded == true)
            return "Daily budget limit exceeded";

        if (budget?.IsMonthlyLimitExceeded == true)
            return "Monthly budget limit exceeded";

        if (rateLimit != null && rateLimitStats != null)
        {
            if (rateLimit.CallsPerMinute.HasValue && rateLimitStats.TotalCallsLastMinute >= rateLimit.CallsPerMinute)
                return $"Rate limit: {rateLimitStats.TotalCallsLastMinute}/{rateLimit.CallsPerMinute} calls/min";

            if (rateLimit.TokensPerHour.HasValue && rateLimitStats.TotalTokensLastHour >= rateLimit.TokensPerHour)
                return $"Rate limit: {rateLimitStats.TotalTokensLastHour}/{rateLimit.TokensPerHour} tokens/hour";

            if (rateLimitStats.CurrentConcurrentTasks >= rateLimit.MaxConcurrentTasks)
                return $"Concurrent task limit: {rateLimitStats.CurrentConcurrentTasks}/{rateLimit.MaxConcurrentTasks}";
        }

        if (costSummary != null)
        {
            if (costSummary.DailyUsagePercent > 90)
                return $"Daily budget at {costSummary.DailyUsagePercent}%";

            if (costSummary.MonthlyUsagePercent > 90)
                return $"Monthly budget at {costSummary.MonthlyUsagePercent}%";
        }

        return "Healthy";
    }

    private static int EstimateTokens(string userInput)
    {
        // Rough heuristic: ~4 characters per token on average
        // Add 20% overhead for system prompt, formatting
        return (int)((userInput.Length / 4.0) * 1.2);
    }

    private static decimal EstimateCost(LLMProvider provider, int estimatedTokens)
    {
        if (provider == null || estimatedTokens <= 0)
            return 0;

        // Assume 50/50 input/output split for cost estimation
        var inputTokens = estimatedTokens / 2;
        var outputTokens = estimatedTokens / 2;

        var inputCost = (inputTokens / 1000m) * (provider.CostPer1kInputTokens ?? 0);
        var outputCost = (outputTokens / 1000m) * (provider.CostPer1kOutputTokens ?? 0);

        return inputCost + outputCost;
    }
}

public class TokenBudgetCheckResult
{
    public bool IsAllowed { get; set; } = true;
    public bool ShouldQueue { get; set; }
    public bool Degraded { get; set; }
    public string? BlockReason { get; set; }
    public int EstimatedTokens { get; set; }
    public decimal EstimatedCostUSD { get; set; }
}

public class TokenSpendingForecast
{
    public int AgentId { get; set; }

    // Daily spending
    public decimal CurrentDailySpentUSD { get; set; }
    public decimal DailyLimitUSD { get; set; }
    public decimal DailyRemainingUSD { get; set; }
    public int DailyUsagePercent { get; set; }

    // Monthly spending
    public decimal CurrentMonthlySpentUSD { get; set; }
    public decimal MonthlyLimitUSD { get; set; }
    public decimal MonthlyRemainingUSD { get; set; }
    public int MonthlyUsagePercent { get; set; }
    public decimal ProjectedMonthlyUSD { get; set; }

    // Rate limits
    public int CurrentCallsPerMinute { get; set; }
    public int CallsPerMinuteLimit { get; set; }

    public int CurrentTokensPerHour { get; set; }
    public int TokensPerHourLimit { get; set; }

    public int CurrentConcurrentTasks { get; set; }
    public int MaxConcurrentTasks { get; set; }

    public int QueuedTasksCount { get; set; }

    public bool CanExecuteMore { get; set; }
    public string StatusMessage { get; set; } = "Unknown";
    public DateTime AsOfDate { get; set; }
}
