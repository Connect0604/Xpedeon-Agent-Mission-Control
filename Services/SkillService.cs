using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class SkillService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SkillService(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<List<SkillDefinition>> GetAllAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.SkillDefinitions
            .Where(s => s.IsEnabled)
            .OrderByDescending(s => s.IsBuiltIn)
            .ThenBy(s => s.Name)
            .ToListAsync();
    }

    public async Task<List<SkillDefinition>> GetByIdsAsync(IEnumerable<string> ids)
    {
        var skillIds = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (!skillIds.Any())
            return new List<SkillDefinition>();

        await using var db = _factory.CreateDbContext();
        return await db.SkillDefinitions
            .Where(s => skillIds.Contains(s.Id) && s.IsEnabled)
            .OrderBy(s => s.Name)
            .ToListAsync();
    }

    public async Task<List<SkillDefinition>> GetForAgentAsync(string agentId)
    {
        await using var db = _factory.CreateDbContext();
        return await db.AgentSkillAssignments
            .Where(a => a.AgentId == agentId && a.IsEnabled && a.SkillDefinition != null && a.SkillDefinition.IsEnabled)
            .Include(a => a.SkillDefinition)
            .Select(a => a.SkillDefinition!)
            .OrderBy(s => s.Name)
            .ToListAsync();
    }

    public async Task SetAgentSkillsAsync(string agentId, IEnumerable<string> skillIds)
    {
        var targetIds = skillIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToHashSet();

        await using var db = _factory.CreateDbContext();
        var existing = await db.AgentSkillAssignments.Where(a => a.AgentId == agentId).ToListAsync();

        var toRemove = existing.Where(a => !targetIds.Contains(a.SkillDefinitionId)).ToList();
        if (toRemove.Any())
            db.AgentSkillAssignments.RemoveRange(toRemove);

        var existingIds = existing.Select(a => a.SkillDefinitionId).ToHashSet();
        foreach (var skillId in targetIds.Where(id => !existingIds.Contains(id)))
        {
            db.AgentSkillAssignments.Add(new AgentSkillAssignment
            {
                AgentId = agentId,
                SkillDefinitionId = skillId,
                IsEnabled = true,
                AddedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
    }

    public static List<string> ParseAllowedToolNames(SkillDefinition skill)
    {
        if (string.IsNullOrWhiteSpace(skill.AllowedMcpToolNamesJson))
            return new List<string>();

        try
        {
            return JsonSerializer.Deserialize<List<string>>(skill.AllowedMcpToolNamesJson) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}
