using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class TaskExecutionEventService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public TaskExecutionEventService(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<List<TaskExecutionEvent>> GetForTaskAsync(string taskId)
    {
        await using var db = _factory.CreateDbContext();
        return await db.TaskExecutionEvents
            .Where(e => e.TaskId == taskId)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync();
    }

    public async Task LogAsync(
        string taskId,
        string agentId,
        TaskExecutionEventType eventType,
        string summary,
        object? details = null,
        string? skillDefinitionId = null,
        string? skillName = null,
        string? mcpServerId = null,
        string? mcpServerName = null,
        string? toolName = null,
        string? relatedTaskId = null)
    {
        await using var db = _factory.CreateDbContext();
        db.TaskExecutionEvents.Add(new TaskExecutionEvent
        {
            Id = $"tev-{Guid.NewGuid():N}"[..16],
            TaskId = taskId,
            AgentId = agentId,
            EventType = eventType,
            Summary = summary,
            DetailsJson = details == null ? null : JsonSerializer.Serialize(details, JsonOptions),
            SkillDefinitionId = skillDefinitionId,
            SkillName = skillName,
            MCPServerId = mcpServerId,
            MCPServerName = mcpServerName,
            ToolName = toolName,
            RelatedTaskId = relatedTaskId,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
