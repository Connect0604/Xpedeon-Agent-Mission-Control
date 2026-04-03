using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class SwarmService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly AgentService _agentService;
    private readonly TaskService _taskService;
    private readonly RealtimeService _realtime;

    public SwarmService(IDbContextFactory<AppDbContext> factory, AgentService agentService, TaskService taskService, RealtimeService realtime)
    {
        _factory = factory;
        _agentService = agentService;
        _taskService = taskService;
        _realtime = realtime;
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

        await _realtime.DashboardRefreshAsync();
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
                foreach (var agent in activeAgents)
                {
                    var task = await _taskService.CreateAndRunAsync(agent.Id, taskName, input, priority, TriggerSource.Swarm);
                    await WaitForTaskCompletionAsync(task.Id);
                }
                break;

            case SwarmStrategy.Voting:
                var votingTasks = new List<AgentTask>();
                foreach (var agent in activeAgents)
                    votingTasks.Add(await _taskService.CreateAndRunAsync(agent.Id, taskName, input, priority, TriggerSource.Swarm));

                var votingResults = await WaitForTaskCompletionAsync(votingTasks.Select(t => t.Id));
                await LogSwarmOutcomeAsync(swarm, taskName, DetermineWinningOutput(votingResults, "Voting"));
                break;

            case SwarmStrategy.Pipeline:
                var pipelineInput = input;
                foreach (var agent in activeAgents)
                {
                    var task = await _taskService.CreateAndRunAsync(agent.Id, taskName, pipelineInput, priority, TriggerSource.Swarm);
                    var completed = await WaitForTaskCompletionAsync(task.Id);
                    if (!string.IsNullOrWhiteSpace(completed?.Output))
                        pipelineInput = completed.Output;
                }
                await LogSwarmOutcomeAsync(swarm, taskName, pipelineInput);
                break;

            case SwarmStrategy.OrchestratorWorker:
                var orchestrator = activeAgents.First();
                var workerSeedTask = await _taskService.CreateAndRunAsync(orchestrator.Id, $"{taskName} - orchestration", input, priority, TriggerSource.Swarm);
                var orchestrated = await WaitForTaskCompletionAsync(workerSeedTask.Id);
                var workerInput = orchestrated?.Output ?? input;

                var workerTasks = new List<AgentTask>();
                foreach (var worker in activeAgents.Skip(1))
                    workerTasks.Add(await _taskService.CreateAndRunAsync(worker.Id, taskName, workerInput, priority, TriggerSource.Swarm));

                var workerResults = workerTasks.Any()
                    ? await WaitForTaskCompletionAsync(workerTasks.Select(t => t.Id))
                    : new List<AgentTask>();

                await LogSwarmOutcomeAsync(swarm, taskName, DetermineWinningOutput(workerResults, "Orchestrator"));
                break;

            default:
                foreach (var agent in activeAgents)
                    await _taskService.CreateAndRunAsync(agent.Id, taskName, input, priority, TriggerSource.Swarm);
                break;
        }

        await _realtime.DashboardRefreshAsync();
    }

    public async Task DeleteSwarmAsync(string id)
    {
        await using var db = _factory.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync();

        var swarm = await db.Swarms.Include(s => s.Agents).FirstOrDefaultAsync(s => s.Id == id);
        if (swarm == null) return;

        var agentIds = swarm.Agents.Select(a => a.Id).ToList();
        var taskIds = agentIds.Any()
            ? await db.Tasks.Where(t => agentIds.Contains(t.AgentId)).Select(t => t.Id).ToListAsync()
            : new List<string>();

        if (taskIds.Any())
        {
            var feedbacks = await db.TaskFeedbacks
                .Where(f => taskIds.Contains(f.TaskId) || agentIds.Contains(f.AgentId))
                .ToListAsync();
            if (feedbacks.Any())
            {
                db.TaskFeedbacks.RemoveRange(feedbacks);
            }

            var taskLogs = await db.Logs
                .Where(l => taskIds.Contains(l.TaskId!))
                .ToListAsync();
            if (taskLogs.Any())
            {
                db.Logs.RemoveRange(taskLogs);
            }
        }

        if (agentIds.Any())
        {
            var agentLogs = await db.Logs
                .Where(l => l.AgentId != null && agentIds.Contains(l.AgentId))
                .ToListAsync();
            if (agentLogs.Any())
            {
                db.Logs.RemoveRange(agentLogs);
            }

            var memories = await db.AgentMemories
                .Where(m => agentIds.Contains(m.AgentId) || m.SwarmId == id)
                .ToListAsync();
            if (memories.Any())
            {
                db.AgentMemories.RemoveRange(memories);
            }

            var schedules = await db.AgentSchedules
                .Where(s => agentIds.Contains(s.AgentId))
                .ToListAsync();
            if (schedules.Any())
            {
                db.AgentSchedules.RemoveRange(schedules);
            }

            var mcpLinks = await db.AgentMCPServers
                .Where(m => agentIds.Contains(m.AgentId))
                .ToListAsync();
            if (mcpLinks.Any())
            {
                db.AgentMCPServers.RemoveRange(mcpLinks);
            }

            var tools = await db.AgentTools
                .Where(t => agentIds.Contains(t.AgentId))
                .ToListAsync();
            if (tools.Any())
            {
                db.AgentTools.RemoveRange(tools);
            }

            var prompts = await db.PromptHistories
                .Where(p => agentIds.Contains(p.AgentId))
                .ToListAsync();
            if (prompts.Any())
            {
                db.PromptHistories.RemoveRange(prompts);
            }

            var tasks = await db.Tasks
                .Where(t => agentIds.Contains(t.AgentId))
                .ToListAsync();
            if (tasks.Any())
            {
                db.Tasks.RemoveRange(tasks);
            }

            db.Agents.RemoveRange(swarm.Agents);
        }

        db.Swarms.Remove(swarm);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        await _realtime.DashboardRefreshAsync();
    }

    private async Task<AgentTask?> WaitForTaskCompletionAsync(string taskId, int timeoutSeconds = 180)
    {
        var results = await WaitForTaskCompletionAsync(new[] { taskId }, timeoutSeconds);
        return results.FirstOrDefault();
    }

    private async Task<List<AgentTask>> WaitForTaskCompletionAsync(IEnumerable<string> taskIds, int timeoutSeconds = 180)
    {
        var ids = taskIds.ToList();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            await using var db = _factory.CreateDbContext();
            var tasks = await db.Tasks.Where(t => ids.Contains(t.Id)).ToListAsync();
            if (tasks.All(t => t.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled))
                return tasks;

            await Task.Delay(1000);
        }

        await using var timeoutDb = _factory.CreateDbContext();
        return await timeoutDb.Tasks.Where(t => ids.Contains(t.Id)).ToListAsync();
    }

    private static string DetermineWinningOutput(List<AgentTask> tasks, string strategyName)
    {
        var outputs = tasks
            .Where(t => t.Status == AgentTaskStatus.Completed && !string.IsNullOrWhiteSpace(t.Output))
            .Select(t => t.Output!)
            .ToList();

        if (!outputs.Any())
            return $"{strategyName} swarm finished, but no successful outputs were produced.";

        return outputs
            .GroupBy(o => o)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key.Length)
            .First()
            .Key;
    }

    private async Task LogSwarmOutcomeAsync(Swarm swarm, string taskName, string outcome)
    {
        await using var db = _factory.CreateDbContext();
        db.Logs.Add(new LogEntry
        {
            AgentId = null,
            AgentName = swarm.Name,
            Message = $"Swarm '{swarm.Name}' finished '{taskName}': {outcome}",
            Level = AgentLogLevel.Info,
            Timestamp = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
