using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class AgentService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public AgentService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public async Task<List<Agent>> GetAllAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.Agents
            .Include(a => a.LLMProvider)
            .Include(a => a.Swarm)
            .Include(a => a.Tools)
            .Include(a => a.MCPServers).ThenInclude(m => m.MCPServer)
            .Include(a => a.Schedule)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();
    }

    public async Task<Agent?> GetByIdAsync(string id)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Agents
            .Include(a => a.LLMProvider)
            .Include(a => a.Swarm)
            .Include(a => a.Tools)
            .Include(a => a.MCPServers).ThenInclude(m => m.MCPServer)
            .Include(a => a.Schedule)
            .Include(a => a.PromptHistory.OrderByDescending(p => p.Version).Take(10))
            .FirstOrDefaultAsync(a => a.Id == id);
    }

    public async Task<Agent> CreateAsync(Agent agent, List<string>? mcpServerIds = null)
    {
        await using var db = _factory.CreateDbContext();

        agent.Id = $"ag-{Guid.NewGuid():N}"[..12];
        agent.CreatedAt = DateTime.UtcNow;
        agent.StartedAt = DateTime.UtcNow;
        agent.LastSeen = DateTime.UtcNow;
        agent.Status = AgentStatus.Idle;

        // Attach MCP servers
        if (mcpServerIds?.Any() == true)
        {
            agent.MCPServers = mcpServerIds.Select(id => new AgentMCPServer
            {
                AgentId = agent.Id,
                MCPServerId = id
            }).ToList();
        }

        // Save initial prompt version
        if (!string.IsNullOrWhiteSpace(agent.SystemPrompt))
        {
            agent.PromptHistory = new List<PromptHistory>
            {
                new()
                {
                    AgentId = agent.Id,
                    Version = 1,
                    Prompt = agent.SystemPrompt,
                    ChangeReason = PromptChangeReason.Manual,
                    ChangeNote = "Initial prompt",
                    IsActive = true
                }
            };
        }

        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        await LogAsync(db, agent.Id, agent.Name, $"Agent '{agent.Name}' created", AgentLogLevel.Info);
        await db.SaveChangesAsync();

        return agent;
    }

    public async Task<Agent> UpdateAsync(Agent agent)
    {
        await using var db = _factory.CreateDbContext();
        var existing = await db.Agents.Include(a => a.PromptHistory)
                                      .FirstOrDefaultAsync(a => a.Id == agent.Id)
                       ?? throw new InvalidOperationException("Agent not found");

        // Track prompt change
        if (existing.SystemPrompt != agent.SystemPrompt)
        {
            var version = existing.PromptHistory.Any()
                ? existing.PromptHistory.Max(p => p.Version) + 1 : 1;

            // Deactivate current
            foreach (var ph in existing.PromptHistory.Where(p => p.IsActive))
                ph.IsActive = false;

            db.PromptHistories.Add(new PromptHistory
            {
                AgentId = agent.Id,
                Version = version,
                Prompt = agent.SystemPrompt,
                PreviousPrompt = existing.SystemPrompt,
                ChangeReason = PromptChangeReason.Manual,
                ChangeNote = "Manual update",
                SuccessRateAtChange = existing.SuccessRate,
                IsActive = true
            });
        }

        db.Entry(existing).CurrentValues.SetValues(agent);
        await db.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteAsync(string id)
    {
        await using var db = _factory.CreateDbContext();
        var agent = await db.Agents.FindAsync(id);
        if (agent != null)
        {
            db.Agents.Remove(agent);
            await db.SaveChangesAsync();
        }
    }

    public async Task UpdateStatusAsync(string id, AgentStatus status)
    {
        await using var db = _factory.CreateDbContext();
        var agent = await db.Agents.FindAsync(id);
        if (agent == null) return;
        agent.Status = status;
        agent.LastSeen = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await LogAsync(db, id, agent.Name, $"Status changed to {status}", AgentLogLevel.Info);
        await db.SaveChangesAsync();
    }

    public async Task UpdateMetricsAsync(string id, double cpu, double memMB)
    {
        await using var db = _factory.CreateDbContext();
        var agent = await db.Agents.FindAsync(id);
        if (agent == null) return;
        agent.CpuUsage = cpu;
        agent.MemoryUsageMB = memMB;
        agent.LastSeen = DateTime.UtcNow;
        agent.CpuHistory.Add(Math.Round(cpu, 1));
        if (agent.CpuHistory.Count > 20) agent.CpuHistory.RemoveAt(0);
        await db.SaveChangesAsync();
    }

    public async Task<DashboardSummary> GetSummaryAsync()
    {
        await using var db = _factory.CreateDbContext();
        var today = DateTime.UtcNow.Date;
        var agents = await db.Agents.ToListAsync();
        var tasks = await db.Tasks.Where(t => t.CreatedAt >= today).ToListAsync();
        var swarms = await db.Swarms.ToListAsync();

        return new DashboardSummary
        {
            TotalAgents = agents.Count,
            ActiveAgents = agents.Count(a => a.Status == AgentStatus.Active),
            IdleAgents = agents.Count(a => a.Status == AgentStatus.Idle),
            WarningAgents = agents.Count(a => a.Status == AgentStatus.Warning),
            ErrorAgents = agents.Count(a => a.Status == AgentStatus.Error),
            OfflineAgents = agents.Count(a => a.Status == AgentStatus.Offline),
            TotalTasksToday = tasks.Count,
            RunningTasks = tasks.Count(t => t.Status == AgentTaskStatus.Running),
            QueuedTasks = tasks.Count(t => t.Status == AgentTaskStatus.Queued),
            CompletedTasksToday = tasks.Count(t => t.Status == AgentTaskStatus.Completed),
            FailedTasksToday = tasks.Count(t => t.Status == AgentTaskStatus.Failed),
            PendingApprovals = tasks.Count(t => t.Status == AgentTaskStatus.PendingApproval),
            AverageSuccessRate = agents.Any() ? agents.Average(a => a.SuccessRate) : 100,
            TotalCpuUsage = agents.Any(a => a.Status == AgentStatus.Active)
                ? agents.Where(a => a.Status == AgentStatus.Active).Average(a => a.CpuUsage) : 0,
            TotalTokensToday = tasks.Sum(t => t.TotalTokens),
            TotalCostToday = tasks.Sum(t => t.CostUSD),
            TotalSwarms = swarms.Count,
            ActiveSwarms = swarms.Count(s => s.IsActive)
        };
    }

    private static Task LogAsync(AppDbContext db, string? agentId, string agentName, string message, AgentLogLevel level)
    {
        db.Logs.Add(new LogEntry
        {
            AgentId = agentId,
            AgentName = agentName,
            Message = message,
            Level = level,
            Timestamp = DateTime.UtcNow
        });
        return Task.CompletedTask;
    }
}
