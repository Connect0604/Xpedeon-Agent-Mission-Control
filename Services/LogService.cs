using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class LogService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly RealtimeService _realtime;

    public LogService(IDbContextFactory<AppDbContext> factory, RealtimeService realtime)
    {
        _factory = factory;
        _realtime = realtime;
    }

    public async Task<List<LogEntry>> GetAllAsync(int count = 200)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Logs
            .OrderByDescending(l => l.Timestamp)
            .Take(count)
            .ToListAsync();
    }

    public async Task<List<LogEntry>> GetForAgentAsync(string agentId, int count = 50)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Logs
            .Where(l => l.AgentId == agentId)
            .OrderByDescending(l => l.Timestamp)
            .Take(count)
            .ToListAsync();
    }

    public async Task<List<LogEntry>> SearchAsync(string? search, AgentLogLevel? level, string? agentName, int count = 200)
    {
        await using var db = _factory.CreateDbContext();
        var q = db.Logs.AsQueryable();

        if (level.HasValue)
            q = q.Where(l => l.Level == level.Value);

        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(l => l.Message.Contains(search));

        if (!string.IsNullOrWhiteSpace(agentName))
            q = q.Where(l => l.AgentName.Contains(agentName));

        return await q.OrderByDescending(l => l.Timestamp).Take(count).ToListAsync();
    }

    public async Task AddAsync(string? agentId, string agentName, string message, AgentLogLevel level, string? taskId = null)
    {
        await using var db = _factory.CreateDbContext();
        db.Logs.Add(new LogEntry
        {
            AgentId = agentId,
            AgentName = agentName,
            Message = message,
            Level = level,
            TaskId = taskId,
            Timestamp = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        await _realtime.LogAddedAsync(agentId ?? string.Empty, message, level.ToString());
    }

    public async Task ClearOldAsync(int keepDays = 30)
    {
        await using var db = _factory.CreateDbContext();
        var cutoff = DateTime.UtcNow.AddDays(-keepDays);
        var old = await db.Logs.Where(l => l.Timestamp < cutoff).ToListAsync();
        db.Logs.RemoveRange(old);
        await db.SaveChangesAsync();
    }
}
