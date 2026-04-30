using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

/// <summary>
/// Hosted service that runs health checks in the background every 30 seconds
/// Keeps health status current and ready for /health endpoint
/// </summary>
public class BackgroundHealthCheckService : BackgroundService
{
    private readonly ILogger<BackgroundHealthCheckService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private const int HealthCheckIntervalSeconds = 30;

    public BackgroundHealthCheckService(
        ILogger<BackgroundHealthCheckService> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Background health check service starting (interval: {Seconds}s)", HealthCheckIntervalSeconds);

        // Wait a bit before first check (allow app startup)
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var healthCheckService = scope.ServiceProvider.GetRequiredService<HealthCheckService>();
                    var healthReport = await healthCheckService.GetHealthAsync(forceRefresh: true);

                    // Log summary
                    _logger.LogDebug(
                        "Background health check: {Status} ({Healthy}H, {Degraded}D, {Unhealthy}U)",
                        healthReport.StatusString,
                        healthReport.HealthyCount,
                        healthReport.DegradedCount,
                        healthReport.UnhealthyCount);

                    // Alert on critical status changes
                    if (healthReport.Status == HealthStatus.Unhealthy)
                    {
                        var unhealthyComponents = healthReport.Components
                            .Where(c => c.Status == HealthStatus.Unhealthy)
                            .Select(c => c.Name)
                            .ToList();

                        _logger.LogError(
                            "CRITICAL: System unhealthy. Unhealthy components: {Components}",
                            string.Join(", ", unhealthyComponents));
                    }
                }

                // Wait before next check
                await Task.Delay(TimeSpan.FromSeconds(HealthCheckIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during background health check");
                // Continue checking even if one iteration fails
                await Task.Delay(TimeSpan.FromSeconds(HealthCheckIntervalSeconds), stoppingToken);
            }
        }

        _logger.LogInformation("Background health check service stopped");
    }
}
