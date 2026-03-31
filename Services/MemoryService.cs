using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class MemoryService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public MemoryService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public async Task<List<AgentMemory>> GetForAgentAsync(string agentId, MemoryType? type = null)
    {
        await using var db = _factory.CreateDbContext();
        var query = db.AgentMemories.Where(m => m.AgentId == agentId);
        if (type.HasValue) query = query.Where(m => m.Type == type.Value);
        return await query.OrderByDescending(m => m.CreatedAt).ToListAsync();
    }

    public async Task<List<AgentMemory>> GetSwarmMemoryAsync(string swarmId)
    {
        await using var db = _factory.CreateDbContext();
        return await db.AgentMemories
            .Where(m => m.SwarmId == swarmId && m.Type == MemoryType.Swarm)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();
    }

    public async Task StoreAsync(string agentId, string key, string content, MemoryType type, string? swarmId = null, string? taskId = null)
    {
        await using var db = _factory.CreateDbContext();

        // Update if key exists
        var existing = await db.AgentMemories.FirstOrDefaultAsync(m => m.AgentId == agentId && m.Key == key && m.Type == type);
        if (existing != null)
        {
            existing.Content = content;
            existing.LastAccessedAt = DateTime.UtcNow;
            existing.AccessCount++;
        }
        else
        {
            db.AgentMemories.Add(new AgentMemory
            {
                AgentId = agentId,
                SwarmId = swarmId,
                Type = type,
                Key = key,
                Content = content,
                TaskId = taskId
            });
        }

        await db.SaveChangesAsync();
    }

    public async Task<string?> RecallAsync(string agentId, string key, MemoryType type)
    {
        await using var db = _factory.CreateDbContext();
        var mem = await db.AgentMemories.FirstOrDefaultAsync(m => m.AgentId == agentId && m.Key == key && m.Type == type);
        if (mem == null) return null;
        mem.AccessCount++;
        mem.LastAccessedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return mem.Content;
    }

    public async Task<List<AgentMemory>> SearchAsync(string agentId, string query)
    {
        await using var db = _factory.CreateDbContext();
        // Simple keyword search — can be replaced with vector search later
        return await db.AgentMemories
            .Where(m => m.AgentId == agentId && m.Content.Contains(query))
            .OrderByDescending(m => m.AccessCount)
            .Take(10)
            .ToListAsync();
    }

    public async Task ClearExpiredAsync()
    {
        await using var db = _factory.CreateDbContext();
        var expired = await db.AgentMemories
            .Where(m => m.ExpiresAt.HasValue && m.ExpiresAt < DateTime.UtcNow)
            .ToListAsync();
        db.AgentMemories.RemoveRange(expired);
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(string memoryId)
    {
        await using var db = _factory.CreateDbContext();
        var mem = await db.AgentMemories.FindAsync(memoryId);
        if (mem != null) { db.AgentMemories.Remove(mem); await db.SaveChangesAsync(); }
    }
}
