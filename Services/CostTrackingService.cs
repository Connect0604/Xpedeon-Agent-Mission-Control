using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class CostTrackingService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly AuditService _auditService;
    private readonly ILogger<CostTrackingService> _logger;

    public CostTrackingService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        AuditService auditService,
        ILogger<CostTrackingService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<bool> RecordCostAsync(
        int agentId,
        int taskId,
        decimal costUSD,
        int tokensUsed,
        int? inputTokens = null,
        int? outputTokens = null,
        string? providerName = null,
        string? modelName = null)
    {
        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var budget = await db.AgentBudgets.FirstOrDefaultAsync(b => b.AgentId == agentId);
            if (budget == null)
            {
                _logger.LogWarning("No budget found for agent {AgentId}", agentId);
                return false;
            }

            // Reset daily budget if needed
            if (DateTime.UtcNow >= budget.DailyResetAt)
            {
                budget.TodaySpentUSD = 0;
                budget.DailyResetAt = GetNextDailyReset();
            }

            // Reset monthly budget if needed
            if (DateTime.UtcNow >= budget.MonthlyResetAt)
            {
                budget.MonthSpentUSD = 0;
                budget.ProjectedMonthlyUSD = 0;
                budget.MonthlyResetAt = GetNextMonthlyReset();
            }

            // Update spending
            budget.TodaySpentUSD += costUSD;
            budget.MonthSpentUSD += costUSD;
            budget.LastCheckedAt = DateTime.UtcNow;

            // Update projected monthly spending (linear extrapolation)
            var dayOfMonth = DateTime.UtcNow.Day;
            var daysInMonth = DateTime.DaysInMonth(DateTime.UtcNow.Year, DateTime.UtcNow.Month);
            budget.ProjectedMonthlyUSD = dayOfMonth > 0 ? (budget.MonthSpentUSD / dayOfMonth) * daysInMonth : 0;

            // Record cost event
            var costEvent = new CostEvent
            {
                AgentId = agentId,
                TaskId = taskId,
                CostUSD = costUSD,
                TokensUsed = tokensUsed,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                ProviderName = providerName,
                ModelName = modelName,
                OccurredAt = DateTime.UtcNow
            };

            db.CostEvents.Add(costEvent);
            db.AgentBudgets.Update(budget);
            await db.SaveChangesAsync();

            // Log the cost event
            await _auditService.LogAsync(
                action: "RecordCost",
                entityType: "CostEvent",
                entityId: costEvent.Id.ToString(),
                entityName: $"Task #{taskId}",
                afterSnapshot: costEvent,
                changeDescription: $"Cost ${costUSD:F2} recorded ({tokensUsed} tokens)"
            );

            // Check for alerts
            await GenerateAlertsAsync(agentId, budget);

            return true;
        }
    }

    public async Task<bool> CheckBudgetAsync(int agentId, decimal requestedCostUSD)
    {
        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var budget = await db.AgentBudgets.FirstOrDefaultAsync(b => b.AgentId == agentId);
            if (budget == null)
                return true; // No budget = unlimited

            // Check if daily limit exceeded
            if (budget.HasDailyLimit && (budget.TodaySpentUSD + requestedCostUSD) > budget.DailyCostLimitUSD!.Value)
            {
                _logger.LogWarning(
                    "Daily budget exceeded for agent {AgentId}: {Current} + {Requested} > {Limit}",
                    agentId, budget.TodaySpentUSD, requestedCostUSD, budget.DailyCostLimitUSD!.Value);
                return false;
            }

            // Check if monthly limit exceeded
            if (budget.HasMonthlyLimit && (budget.MonthSpentUSD + requestedCostUSD) > budget.MonthlyCostLimitUSD!.Value)
            {
                _logger.LogWarning(
                    "Monthly budget exceeded for agent {AgentId}: {Current} + {Requested} > {Limit}",
                    agentId, budget.MonthSpentUSD, requestedCostUSD, budget.MonthlyCostLimitUSD!.Value);
                return false;
            }

            // Check for active overrides that extend limits
            var activeOverride = await db.BudgetOverrides
                .FirstOrDefaultAsync(o =>
                    o.AgentId == agentId &&
                    o.IsActive);

            if (activeOverride != null)
                return true; // Override allows spending

            return true;
        }
    }

    public async Task<CostSummary?> GetCostSummaryAsync(int agentId)
    {
        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var budget = await db.AgentBudgets.FirstOrDefaultAsync(b => b.AgentId == agentId);
            if (budget == null)
                return null;

            // Reset counters if needed
            if (DateTime.UtcNow >= budget.DailyResetAt)
                budget.TodaySpentUSD = 0;

            if (DateTime.UtcNow >= budget.MonthlyResetAt)
                budget.MonthSpentUSD = 0;

            return new CostSummary
            {
                AgentId = agentId,
                TodaySpentUSD = budget.TodaySpentUSD,
                MonthSpentUSD = budget.MonthSpentUSD,
                ProjectedMonthlyUSD = budget.ProjectedMonthlyUSD,
                DailyLimitUSD = budget.DailyCostLimitUSD ?? 0,
                MonthlyLimitUSD = budget.MonthlyCostLimitUSD ?? 0,
                DailyUsagePercent = budget.DailyUsagePercent,
                MonthlyUsagePercent = budget.MonthlyUsagePercent,
                AsOfDate = DateTime.UtcNow
            };
        }
    }

    public async Task<bool> ApplyOverrideAsync(
        int agentId,
        decimal overrideAmountUSD,
        string period,
        string reason,
        string approvedBy,
        string? ticketRef = null,
        DateTime? expiresAt = null)
    {
        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var @override = new BudgetOverride
            {
                AgentId = agentId,
                OverrideAmountUSD = overrideAmountUSD,
                Period = period,
                Reason = reason,
                ApprovedBy = approvedBy,
                TicketRef = ticketRef,
                ExpiresAt = expiresAt,
                CreatedAt = DateTime.UtcNow
            };

            db.BudgetOverrides.Add(@override);
            await db.SaveChangesAsync();

            await _auditService.LogAsync(
                action: "ApplyBudgetOverride",
                entityType: "BudgetOverride",
                entityId: @override.Id.ToString(),
                entityName: $"Agent #{agentId}",
                afterSnapshot: @override,
                changeDescription: $"Override ${overrideAmountUSD:F2} applied ({period}) by {approvedBy}"
            );

            return true;
        }
    }

    public async Task InitializeBudgetAsync(int agentId, decimal? dailyLimitUSD = null, decimal? monthlyLimitUSD = null)
    {
        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var existingBudget = await db.AgentBudgets.FirstOrDefaultAsync(b => b.AgentId == agentId);
            if (existingBudget != null)
                return; // Budget already exists

            var budget = new AgentBudget
            {
                AgentId = agentId,
                DailyCostLimitUSD = dailyLimitUSD,
                MonthlyCostLimitUSD = monthlyLimitUSD,
                AlertThresholdPercent = 75,
                TodaySpentUSD = 0,
                MonthSpentUSD = 0,
                ProjectedMonthlyUSD = 0,
                LastCheckedAt = DateTime.UtcNow,
                DailyResetAt = GetNextDailyReset(),
                MonthlyResetAt = GetNextMonthlyReset()
            };

            db.AgentBudgets.Add(budget);
            await db.SaveChangesAsync();

            await _auditService.LogAsync(
                action: "InitializeBudget",
                entityType: "AgentBudget",
                entityId: budget.Id.ToString(),
                entityName: $"Agent #{agentId}",
                afterSnapshot: budget,
                changeDescription: $"Budget initialized: Daily ${dailyLimitUSD:F2}, Monthly ${monthlyLimitUSD:F2}"
            );
        }
    }

    private async Task GenerateAlertsAsync(int agentId, AgentBudget budget)
    {
        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            // Check daily alerts
            if (budget.HasDailyLimit)
            {
                var dailyPercent = (int)((budget.TodaySpentUSD / budget.DailyCostLimitUSD!.Value) * 100);
                await CheckAndCreateAlertAsync(db, agentId, dailyPercent, "Daily", budget.TodaySpentUSD, budget.DailyCostLimitUSD!.Value);
            }

            // Check monthly alerts
            if (budget.HasMonthlyLimit)
            {
                var monthlyPercent = (int)((budget.MonthSpentUSD / budget.MonthlyCostLimitUSD!.Value) * 100);
                await CheckAndCreateAlertAsync(db, agentId, monthlyPercent, "Monthly", budget.MonthSpentUSD, budget.MonthlyCostLimitUSD!.Value);
            }
        }
    }

    private async Task CheckAndCreateAlertAsync(
        AppDbContext db,
        int agentId,
        int usagePercent,
        string period,
        decimal currentSpent,
        decimal limit)
    {
        var thresholds = new[] { 50, 75, 90, 100 };
        foreach (var threshold in thresholds)
        {
            if (usagePercent >= threshold)
            {
                // Check if alert already created
                var existingAlert = await db.CostAlerts.FirstOrDefaultAsync(a =>
                    a.AgentId == agentId &&
                    a.Period == period &&
                    a.ThresholdPercent == threshold &&
                    a.CreatedAt.Date == DateTime.UtcNow.Date);

                if (existingAlert == null)
                {
                    var alert = new CostAlert
                    {
                        AgentId = agentId,
                        ThresholdPercent = threshold,
                        Period = period,
                        CurrentSpentUSD = currentSpent,
                        LimitUSD = limit,
                        Message = $"{period} budget {threshold}% spent: ${currentSpent:F2} of ${limit:F2}",
                        IsNotified = false,
                        CreatedAt = DateTime.UtcNow
                    };

                    db.CostAlerts.Add(alert);
                    await db.SaveChangesAsync();

                    _logger.LogWarning(
                        "Budget alert created for agent {AgentId}: {Period} {Percent}% threshold",
                        agentId, period, threshold);
                }
            }
        }
    }

    private static DateTime GetNextDailyReset()
    {
        var now = DateTime.UtcNow;
        var tomorrow = now.AddDays(1);
        return new DateTime(tomorrow.Year, tomorrow.Month, tomorrow.Day, 0, 0, 0, DateTimeKind.Utc);
    }

    private static DateTime GetNextMonthlyReset()
    {
        var now = DateTime.UtcNow;
        var nextMonth = now.AddMonths(1);
        return new DateTime(nextMonth.Year, nextMonth.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    }
}
