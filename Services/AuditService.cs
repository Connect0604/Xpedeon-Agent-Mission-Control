using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

/// <summary>
/// Service for logging all CRUD operations to audit trail
/// Provides searchable audit logs for compliance and debugging
/// </summary>
public class AuditService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger<AuditService> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AuditService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ILogger<AuditService> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Log a CRUD operation
    /// </summary>
    public async Task LogAsync(
        string action,
        string entityType,
        string entityId,
        string? entityName = null,
        object? beforeSnapshot = null,
        object? afterSnapshot = null,
        string? changeDescription = null,
        string? errorMessage = null,
        string? metadataJson = null)
    {
        try
        {
            var httpContext = _httpContextAccessor.HttpContext;
            var userId = httpContext?.User?.Identity?.Name ?? "system";
            var ipAddress = httpContext?.Connection?.RemoteIpAddress?.ToString();
            var userAgent = httpContext?.Request?.Headers["User-Agent"].ToString();
            var requestPath = httpContext?.Request?.Path.Value;
            var httpMethod = httpContext?.Request?.Method;

            var auditLog = new AuditLog
            {
                Action = action,
                EntityType = entityType,
                EntityId = entityId,
                EntityName = entityName,
                BeforeJson = beforeSnapshot != null ? JsonSerializer.Serialize(beforeSnapshot) : null,
                AfterJson = afterSnapshot != null ? JsonSerializer.Serialize(afterSnapshot) : null,
                ChangeDescription = changeDescription,
                UserId = userId,
                IpAddress = ipAddress,
                UserAgent = userAgent,
                RequestPath = requestPath,
                HttpMethod = httpMethod,
                ErrorMessage = errorMessage,
                MetadataJson = metadataJson,
                Timestamp = DateTime.UtcNow
            };

            using (var db = await _dbContextFactory.CreateDbContextAsync())
            {
                db.Set<AuditLog>().Add(auditLog);
                await db.SaveChangesAsync();
            }

            _logger.LogInformation(
                "Audit: {Action} {EntityType} {EntityId} by {UserId} from {IpAddress}",
                action, entityType, entityId, userId, ipAddress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write audit log for {EntityType} {EntityId}", entityType, entityId);
            // Don't throw - audit logging should not break main operations
        }
    }

    /// <summary>
    /// Search audit logs
    /// </summary>
    public async Task<List<AuditLog>> SearchAsync(
        string? userId = null,
        string? action = null,
        string? entityType = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int skip = 0,
        int take = 100)
    {
        try
        {
            using (var db = await _dbContextFactory.CreateDbContextAsync())
            {
                var query = db.Set<AuditLog>().AsQueryable();

                if (!string.IsNullOrEmpty(userId))
                    query = query.Where(a => a.UserId == userId);

                if (!string.IsNullOrEmpty(action))
                    query = query.Where(a => a.Action == action);

                if (!string.IsNullOrEmpty(entityType))
                    query = query.Where(a => a.EntityType == entityType);

                if (fromDate.HasValue)
                    query = query.Where(a => a.Timestamp >= fromDate.Value);

                if (toDate.HasValue)
                    query = query.Where(a => a.Timestamp <= toDate.Value);

                return await query
                    .OrderByDescending(a => a.Timestamp)
                    .Skip(skip)
                    .Take(take)
                    .ToListAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search audit logs");
            return new List<AuditLog>();
        }
    }

    /// <summary>
    /// Get audit logs for a specific entity
    /// </summary>
    public async Task<List<AuditLog>> GetEntityHistoryAsync(
        string entityType,
        string entityId,
        int take = 50)
    {
        try
        {
            using (var db = await _dbContextFactory.CreateDbContextAsync())
            {
                return await db.Set<AuditLog>()
                    .Where(a => a.EntityType == entityType && a.EntityId == entityId)
                    .OrderByDescending(a => a.Timestamp)
                    .Take(take)
                    .ToListAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get history for {EntityType} {EntityId}", entityType, entityId);
            return new List<AuditLog>();
        }
    }

    /// <summary>
    /// Get logs for failures (errors/exceptions)
    /// </summary>
    public async Task<List<AuditLog>> GetFailuresAsync(
        DateTime? fromDate = null,
        int take = 50)
    {
        try
        {
            using (var db = await _dbContextFactory.CreateDbContextAsync())
            {
                var query = db.Set<AuditLog>().Where(a => a.IsFailure);

                if (fromDate.HasValue)
                    query = query.Where(a => a.Timestamp >= fromDate.Value);

                return await query
                    .OrderByDescending(a => a.Timestamp)
                    .Take(take)
                    .ToListAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get failure logs");
            return new List<AuditLog>();
        }
    }

    /// <summary>
    /// Get count of actions by type
    /// </summary>
    public async Task<Dictionary<string, int>> GetActionSummaryAsync(DateTime? fromDate = null)
    {
        try
        {
            using (var db = await _dbContextFactory.CreateDbContextAsync())
            {
                var query = db.Set<AuditLog>().AsQueryable();

                if (fromDate.HasValue)
                    query = query.Where(a => a.Timestamp >= fromDate.Value);

                var summary = await query
                    .GroupBy(a => a.Action)
                    .Select(g => new { Action = g.Key, Count = g.Count() })
                    .ToDictionaryAsync(x => x.Action, x => x.Count);

                return summary;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get action summary");
            return new Dictionary<string, int>();
        }
    }

    /// <summary>
    /// Clean up old audit logs (retention policy)
    /// </summary>
    public async Task<int> PruneOldLogsAsync(int retentionDays = 90)
    {
        try
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);

            using (var db = await _dbContextFactory.CreateDbContextAsync())
            {
                var oldLogs = await db.Set<AuditLog>()
                    .Where(a => a.Timestamp < cutoffDate)
                    .ToListAsync();

                if (oldLogs.Count == 0)
                    return 0;

                db.Set<AuditLog>().RemoveRange(oldLogs);
                await db.SaveChangesAsync();

                _logger.LogInformation("Pruned {Count} audit logs older than {Days} days", oldLogs.Count, retentionDays);
                return oldLogs.Count;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prune audit logs");
            return 0;
        }
    }

    /// <summary>
    /// Generate change description from before/after snapshots
    /// </summary>
    public static string? GenerateChangeDescription(object? before, object? after)
    {
        if (before == null || after == null)
            return null;

        var beforeJson = JsonSerializer.Serialize(before);
        var afterJson = JsonSerializer.Serialize(after);

        if (beforeJson == afterJson)
            return "No changes";

        // Simple field-level comparison (could be enhanced)
        var beforeDoc = JsonDocument.Parse(beforeJson);
        var afterDoc = JsonDocument.Parse(afterJson);

        var changes = new List<string>();
        foreach (var prop in afterDoc.RootElement.EnumerateObject())
        {
            if (beforeDoc.RootElement.TryGetProperty(prop.Name, out var beforeProp))
            {
                if (beforeProp.ToString() != prop.Value.ToString())
                {
                    changes.Add($"{prop.Name}: '{beforeProp}' → '{prop.Value}'");
                }
            }
            else
            {
                changes.Add($"{prop.Name}: (new) = '{prop.Value}'");
            }
        }

        return changes.Count > 0 ? string.Join("; ", changes) : "No detectable changes";
    }
}
