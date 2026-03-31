using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class TemplateService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public TemplateService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public async Task<List<AgentTemplate>> GetAllAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.AgentTemplates.OrderByDescending(t => t.IsXpedeonBuiltIn).ThenByDescending(t => t.UsageCount).ToListAsync();
    }

    public async Task<List<AgentTemplate>> GetByCategory(TemplateCategory category)
    {
        await using var db = _factory.CreateDbContext();
        return await db.AgentTemplates.Where(t => t.Category == category).ToListAsync();
    }

    public async Task<AgentTemplate?> GetByIdAsync(string id)
    {
        await using var db = _factory.CreateDbContext();
        return await db.AgentTemplates.FindAsync(id);
    }

    public async Task<Agent> CreateAgentFromTemplateAsync(string templateId, string agentName, string? llmProviderId)
    {
        await using var db = _factory.CreateDbContext();
        var template = await db.AgentTemplates.FindAsync(templateId)
                       ?? throw new InvalidOperationException("Template not found");

        template.UsageCount++;
        await db.SaveChangesAsync();

        return new Agent
        {
            Name = agentName,
            Type = template.DefaultType,
            Description = template.Description,
            SystemPrompt = template.SystemPrompt,
            LLMProviderId = llmProviderId,
            TemplateId = templateId,
            Version = "1.0.0"
        };
    }

    public async Task<AgentTemplate> SaveCustomTemplateAsync(AgentTemplate template)
    {
        await using var db = _factory.CreateDbContext();
        template.Id = Guid.NewGuid().ToString();
        template.IsXpedeonBuiltIn = false;
        template.CreatedAt = DateTime.UtcNow;
        db.AgentTemplates.Add(template);
        await db.SaveChangesAsync();
        return template;
    }
}
