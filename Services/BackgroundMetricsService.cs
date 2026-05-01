using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;

namespace XpedeonAgentMissionControl.Services;

public class BackgroundMetricsService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BackgroundMetricsService> _logger;

    public BackgroundMetricsService(IServiceProvider serviceProvider, ILogger<BackgroundMetricsService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BackgroundMetricsService started");

        // Wait for startup
        await Task.Delay(10000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CollectAndEvaluateAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in BackgroundMetricsService");
            }

            // Collect metrics every 5 minutes
            await Task.Delay(300000, stoppingToken);
        }

        _logger.LogInformation("BackgroundMetricsService stopped");
    }

    private async Task CollectAndEvaluateAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var observabilityService = scope.ServiceProvider.GetRequiredService<ObservabilityService>();

        // Get all active agent IDs
        using var dbContext = db.CreateDbContext();
        var agentIds = await dbContext.Agents
            .Where(a => a.Status == Models.AgentStatus.Active || a.Status == Models.AgentStatus.Idle)
            .Select(a => a.Id)
            .ToListAsync();

        _logger.LogDebug("Collecting metrics for {Count} agents", agentIds.Count);

        foreach (var agentId in agentIds)
        {
            await observabilityService.CollectMetricsAsync(agentId);
            await observabilityService.CheckSLAAsync(agentId);
        }
    }
}
