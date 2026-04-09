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

    public async Task<List<SkillDefinition>> GetAllAsync(bool includeDisabled = false)
    {
        await using var db = _factory.CreateDbContext();
        var query = db.SkillDefinitions.AsQueryable();
        if (!includeDisabled)
            query = query.Where(s => s.IsEnabled);

        return await query
            .OrderByDescending(s => s.IsBuiltIn)
            .ThenBy(s => s.Category)
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

    public async Task<SkillDefinition> CreateAsync(SkillDefinition skill)
    {
        await using var db = _factory.CreateDbContext();
        await ValidateAsync(db, skill, null);

        var entity = new SkillDefinition
        {
            Id = $"skill-{Guid.NewGuid():N}"[..18],
            Name = skill.Name.Trim(),
            Category = skill.Category,
            Description = skill.Description.Trim(),
            PromptSnippet = skill.PromptSnippet?.Trim() ?? string.Empty,
            AllowedMcpToolNamesJson = NormalizeAllowedToolsJson(skill.AllowedMcpToolNamesJson),
            PreferredProviderId = string.IsNullOrWhiteSpace(skill.PreferredProviderId) ? null : skill.PreferredProviderId,
            PreferredModelName = string.IsNullOrWhiteSpace(skill.PreferredModelName) ? null : skill.PreferredModelName.Trim(),
            MaxToolCalls = skill.MaxToolCalls,
            RequiresApproval = skill.RequiresApproval,
            IsBuiltIn = skill.IsBuiltIn,
            IsEnabled = skill.IsEnabled,
            CreatedAt = DateTime.UtcNow
        };

        db.SkillDefinitions.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    public async Task<SkillDefinition> UpdateAsync(SkillDefinition skill)
    {
        await using var db = _factory.CreateDbContext();
        var existing = await db.SkillDefinitions.FirstOrDefaultAsync(s => s.Id == skill.Id)
            ?? throw new InvalidOperationException("Skill not found.");

        await ValidateAsync(db, skill, existing.Id);

        existing.Name = skill.Name.Trim();
        existing.Category = skill.Category;
        existing.Description = skill.Description.Trim();
        existing.PromptSnippet = skill.PromptSnippet?.Trim() ?? string.Empty;
        existing.AllowedMcpToolNamesJson = NormalizeAllowedToolsJson(skill.AllowedMcpToolNamesJson);
        existing.PreferredProviderId = string.IsNullOrWhiteSpace(skill.PreferredProviderId) ? null : skill.PreferredProviderId;
        existing.PreferredModelName = string.IsNullOrWhiteSpace(skill.PreferredModelName) ? null : skill.PreferredModelName.Trim();
        existing.MaxToolCalls = skill.MaxToolCalls;
        existing.RequiresApproval = skill.RequiresApproval;
        existing.IsBuiltIn = skill.IsBuiltIn;
        existing.IsEnabled = skill.IsEnabled;

        await db.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteAsync(string id)
    {
        await using var db = _factory.CreateDbContext();
        var existing = await db.SkillDefinitions.FirstOrDefaultAsync(s => s.Id == id)
            ?? throw new InvalidOperationException("Skill not found.");

        db.SkillDefinitions.Remove(existing);
        await db.SaveChangesAsync();
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

    private static string NormalizeAllowedToolsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "[]";

        try
        {
            var items = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            var normalized = items
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToList();

            return JsonSerializer.Serialize(normalized);
        }
        catch
        {
            throw new InvalidOperationException("Allowed MCP tools must be a valid JSON string array.");
        }
    }

    private static async Task ValidateAsync(AppDbContext db, SkillDefinition skill, string? existingId)
    {
        if (string.IsNullOrWhiteSpace(skill.Name))
            throw new InvalidOperationException("Skill name is required.");

        if (string.IsNullOrWhiteSpace(skill.Description))
            throw new InvalidOperationException("Skill description is required.");

        if (skill.MaxToolCalls is < 0)
            throw new InvalidOperationException("Max tool calls cannot be negative.");

        _ = NormalizeAllowedToolsJson(skill.AllowedMcpToolNamesJson);

        if (!string.IsNullOrWhiteSpace(skill.PreferredProviderId))
        {
            var providerExists = await db.LLMProviders.AnyAsync(p => p.Id == skill.PreferredProviderId);
            if (!providerExists)
                throw new InvalidOperationException("Selected preferred provider was not found.");
        }

        var normalizedName = skill.Name.Trim().ToLower();
        var duplicateExists = await db.SkillDefinitions
            .AnyAsync(s => s.Id != existingId && s.Name.ToLower() == normalizedName);

        if (duplicateExists)
            throw new InvalidOperationException("A skill with the same name already exists.");
    }
}
