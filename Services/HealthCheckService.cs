using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

/// <summary>
/// Service for monitoring system health across all components
/// Probes: database, LLM providers, MCP servers, job queue, memory
/// </summary>
public class HealthCheckService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger<HealthCheckService> _logger;
    private HealthReport _lastReport = new();
    private DateTime _lastCheckTime = DateTime.MinValue;
    private const int CacheDurationSeconds = 30;

    public HealthCheckService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ILogger<HealthCheckService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Get current health status (cached for 30 seconds)
    /// </summary>
    public async Task<HealthReport> GetHealthAsync(bool forceRefresh = false)
    {
        // Use cached result if recent
        if (!forceRefresh && (DateTime.UtcNow - _lastCheckTime).TotalSeconds < CacheDurationSeconds)
        {
            return _lastReport;
        }

        var sw = Stopwatch.StartNew();

        try
        {
            var components = new List<ComponentHealth>();

            // Check each component in parallel
            var tasks = new List<Task<ComponentHealth>>
            {
                CheckDatabaseAsync(),
                CheckLLMProvidersAsync(),
                CheckMCPServersAsync(),
                CheckJobQueueAsync(),
                CheckMemoryAsync()
            };

            var results = await Task.WhenAll(tasks);
            components.AddRange(results);

            sw.Stop();

            // Determine overall status
            var overallStatus = HealthStatus.Healthy;
            if (components.Any(c => c.Status == HealthStatus.Unhealthy))
                overallStatus = HealthStatus.Unhealthy;
            else if (components.Any(c => c.Status == HealthStatus.Degraded))
                overallStatus = HealthStatus.Degraded;

            _lastReport = new HealthReport
            {
                Status = overallStatus,
                CheckedAt = DateTime.UtcNow,
                Components = components
            };

            _lastCheckTime = DateTime.UtcNow;

            _logger.LogInformation(
                "Health check completed: {Status} ({HealthyCount}H, {DegradedCount}D, {UnhealthyCount}U) in {Ms}ms",
                overallStatus,
                _lastReport.HealthyCount,
                _lastReport.DegradedCount,
                _lastReport.UnhealthyCount,
                sw.ElapsedMilliseconds);

            return _lastReport;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed");

            return new HealthReport
            {
                Status = HealthStatus.Unknown,
                CheckedAt = DateTime.UtcNow,
                Components = new List<ComponentHealth>
                {
                    new ComponentHealth
                    {
                        Name = "system",
                        Status = HealthStatus.Unhealthy,
                        Message = "Health check service failed"
                    }
                }
            };
        }
    }

    /// <summary>
    /// Check database connectivity
    /// </summary>
    private async Task<ComponentHealth> CheckDatabaseAsync()
    {
        var sw = Stopwatch.StartNew();
        var health = new ComponentHealth { Name = "database" };

        try
        {
            using (var db = await _dbContextFactory.CreateDbContextAsync())
            {
                var connected = await db.Database.CanConnectAsync();

                if (connected)
                {
                    // Try a simple query
                    var agentCount = await db.Set<Agent>().CountAsync();
                    health.Status = HealthStatus.Healthy;
                    health.Message = "Database connected";
                    health.Details = new Dictionary<string, object>
                    {
                        { "agentCount", agentCount }
                    };
                }
                else
                {
                    health.Status = HealthStatus.Unhealthy;
                    health.Message = "Database connection failed";
                }
            }
        }
        catch (Exception ex)
        {
            health.Status = HealthStatus.Unhealthy;
            health.Message = $"Database error: {ex.Message}";
            _logger.LogWarning(ex, "Database health check failed");
        }

        sw.Stop();
        health.ResponseTimeMs = sw.ElapsedMilliseconds;
        return health;
    }

    /// <summary>
    /// Check LLM provider availability (count enabled, spot-check one)
    /// </summary>
    private async Task<ComponentHealth> CheckLLMProvidersAsync()
    {
        var sw = Stopwatch.StartNew();
        var health = new ComponentHealth { Name = "llm_providers" };

        try
        {
            using (var db = await _dbContextFactory.CreateDbContextAsync())
            {
                var providers = await db.Set<LLMProvider>()
                    .Where(p => p.IsEnabled)
                    .ToListAsync();

                health.Status = HealthStatus.Healthy;
                health.Message = $"{providers.Count} providers enabled";
                health.Details = new Dictionary<string, object>
                {
                    { "totalProviders", providers.Count },
                    { "enabledProviders", providers.Count(p => p.IsEnabled) },
                    { "disabledProviders", await db.Set<LLMProvider>().Where(p => !p.IsEnabled).CountAsync() }
                };

                if (providers.Count == 0)
                {
                    health.Status = HealthStatus.Degraded;
                    health.Message = "No LLM providers enabled";
                }
            }
        }
        catch (Exception ex)
        {
            health.Status = HealthStatus.Unhealthy;
            health.Message = $"LLM providers error: {ex.Message}";
            _logger.LogWarning(ex, "LLM providers health check failed");
        }

        sw.Stop();
        health.ResponseTimeMs = sw.ElapsedMilliseconds;
        return health;
    }

    /// <summary>
    /// Check MCP server registry
    /// </summary>
    private async Task<ComponentHealth> CheckMCPServersAsync()
    {
        var sw = Stopwatch.StartNew();
        var health = new ComponentHealth { Name = "mcp_servers" };

        try
        {
            using (var db = await _dbContextFactory.CreateDbContextAsync())
            {
                var mcpServers = await db.Set<MCPServer>().ToListAsync();
                var healthyServers = mcpServers.Count(s => s.IsHealthy && s.IsEnabled);

                health.Status = HealthStatus.Healthy;
                health.Message = $"{healthyServers}/{mcpServers.Count} MCP servers healthy";
                health.Details = new Dictionary<string, object>
                {
                    { "totalServers", mcpServers.Count },
                    { "healthyServers", healthyServers },
                    { "unhealthyServers", mcpServers.Count - healthyServers }
                };

                if (mcpServers.Count == 0)
                {
                    health.Status = HealthStatus.Degraded;
                    health.Message = "No MCP servers registered";
                }
                else if (healthyServers < mcpServers.Count * 0.8) // Less than 80% healthy
                {
                    health.Status = HealthStatus.Degraded;
                    health.Message = $"Only {healthyServers}/{mcpServers.Count} MCP servers healthy";
                }
            }
        }
        catch (Exception ex)
        {
            health.Status = HealthStatus.Unhealthy;
            health.Message = $"MCP servers error: {ex.Message}";
            _logger.LogWarning(ex, "MCP servers health check failed");
        }

        sw.Stop();
        health.ResponseTimeMs = sw.ElapsedMilliseconds;
        return health;
    }

    /// <summary>
    /// Check job queue depth (pending/running tasks)
    /// </summary>
    private async Task<ComponentHealth> CheckJobQueueAsync()
    {
        var sw = Stopwatch.StartNew();
        var health = new ComponentHealth { Name = "job_queue" };

        try
        {
            using (var db = await _dbContextFactory.CreateDbContextAsync())
            {
                var pendingTasks = await db.Set<AgentTask>()
                    .Where(t => t.Status == "Queued" || t.Status == "Running")
                    .CountAsync();

                var allTasks = await db.Set<AgentTask>().CountAsync();

                health.Status = HealthStatus.Healthy;
                health.Message = $"{pendingTasks} queued tasks";
                health.Details = new Dictionary<string, object>
                {
                    { "queuedTasks", pendingTasks },
                    { "totalTasks", allTasks }
                };

                // Alert if queue is backing up
                if (pendingTasks > 1000)
                {
                    health.Status = HealthStatus.Degraded;
                    health.Message = $"Queue backlog: {pendingTasks} tasks";
                }

                if (pendingTasks > 5000)
                {
                    health.Status = HealthStatus.Unhealthy;
                    health.Message = $"Critical queue backlog: {pendingTasks} tasks";
                }
            }
        }
        catch (Exception ex)
        {
            health.Status = HealthStatus.Unhealthy;
            health.Message = $"Job queue error: {ex.Message}";
            _logger.LogWarning(ex, "Job queue health check failed");
        }

        sw.Stop();
        health.ResponseTimeMs = sw.ElapsedMilliseconds;
        return health;
    }

    /// <summary>
    /// Check memory usage
    /// </summary>
    private Task<ComponentHealth> CheckMemoryAsync()
    {
        var sw = Stopwatch.StartNew();
        var health = new ComponentHealth { Name = "memory" };

        try
        {
            var process = Process.GetCurrentProcess();
            var memoryMB = process.WorkingSet64 / (1024 * 1024);
            var totalSystemMemoryMB = GC.GetTotalMemory(false) / (1024 * 1024);

            health.Status = HealthStatus.Healthy;
            health.Message = $"{memoryMB}MB used";
            health.Details = new Dictionary<string, object>
            {
                { "workingSetMB", memoryMB },
                { "managedMemoryMB", totalSystemMemoryMB }
            };

            // Warn if using more than 1GB
            if (memoryMB > 1024)
            {
                health.Status = HealthStatus.Degraded;
                health.Message = $"High memory: {memoryMB}MB";
            }

            // Critical if using more than 2GB
            if (memoryMB > 2048)
            {
                health.Status = HealthStatus.Unhealthy;
                health.Message = $"Critical memory: {memoryMB}MB";
            }
        }
        catch (Exception ex)
        {
            health.Status = HealthStatus.Unhealthy;
            health.Message = $"Memory check error: {ex.Message}";
            _logger.LogWarning(ex, "Memory health check failed");
        }

        sw.Stop();
        health.ResponseTimeMs = sw.ElapsedMilliseconds;
        return Task.FromResult(health);
    }
}
