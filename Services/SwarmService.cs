using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class SwarmService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly AgentService _agentService;
    private readonly TaskService _taskService;

    public SwarmService(IDbContextFactory<AppDbContext> factory, AgentService agentService, TaskService taskService)
    {
        _factory = factory;
        _agentService = agentService;
        _taskService = taskService;
    }

    public async Task<List<Swarm>> GetAllAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.Swarms.Include(s => s.Agents).OrderByDescending(s => s.CreatedAt).ToListAsync();
    }

    public async Task<Swarm?> GetByIdAsync(string id)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Swarms.Include(s => s.Agents).ThenInclude(a => a.LLMProvider)
                               .FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task<Swarm> CreateSwarmAsync(Swarm swarm, int agentCount)
    {
        await using var db = _factory.CreateDbContext();

        swarm.Id = Guid.NewGuid().ToString();
        swarm.AgentCount = agentCount;
        swarm.CreatedAt = DateTime.UtcNow;
        db.Swarms.Add(swarm);
        await db.SaveChangesAsync();

        // Spawn agents
        for (int i = 1; i <= agentCount; i++)
        {
            var name = swarm.NamePattern
                .Replace("{name}", swarm.Name)
                .Replace("{n}", i.ToString());

            var agent = new Agent
            {
                Name = name,
                Type = AgentType.Custom,
                Description = $"Swarm agent {i} of {swarm.Name}",
                SystemPrompt = swarm.BasePrompt,
                LLMProviderId = swarm.LLMProviderId,
                SwarmId = swarm.Id,
                ShareMemoryWithSwarm = swarm.ShareMemory,
                LongTermMemoryEnabled = swarm.ShareMemory,
                Version = "1.0.0"
            };

            await _agentService.CreateAsync(agent);
        }

        return swarm;
    }

    public async Task DispatchTaskToSwarmAsync(string swarmId, string taskName, string input, TaskPriority priority = TaskPriority.Medium)
    {
        await using var db = _factory.CreateDbContext();
        var swarm = await db.Swarms.Include(s => s.Agents).FirstOrDefaultAsync(s => s.Id == swarmId)
                    ?? throw new InvalidOperationException("Swarm not found");

        var activeAgents = swarm.Agents.Where(a => a.Status is AgentStatus.Active or AgentStatus.Idle).ToList();
        if (!activeAgents.Any()) throw new InvalidOperationException("No active agents in swarm");

        // Dispatch based on strategy
        switch (swarm.Strategy)
        {
            case SwarmStrategy.Parallel:
                foreach (var agent in activeAgents)
                    await _taskService.CreateAndRunAsync(agent.Id, taskName, input, priority, TriggerSource.Swarm);
                break;

            case SwarmStrategy.Sequential:
                var first = activeAgents.First();
                await _taskService.CreateAndRunAsync(first.Id, taskName, input, priority, TriggerSource.Swarm);
                break;

            case SwarmStrategy.Voting:
                foreach (var agent in activeAgents)
                    await _taskService.CreateAndRunAsync(agent.Id, taskName, input, priority, TriggerSource.Swarm);
                break;

            default:
                foreach (var agent in activeAgents)
                    await _taskService.CreateAndRunAsync(agent.Id, taskName, input, priority, TriggerSource.Swarm);
                break;
        }
    }

    public async Task DeleteSwarmAsync(string id)
    {
        await using var db = _factory.CreateDbContext();
        var swarm = await db.Swarms.Include(s => s.Agents).FirstOrDefaultAsync(s => s.Id == id);
        if (swarm == null) return;
        db.Swarms.Remove(swarm);
        await db.SaveChangesAsync();
    }
}
