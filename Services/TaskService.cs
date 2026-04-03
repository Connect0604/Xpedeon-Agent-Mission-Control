using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class TaskService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly LLMExecutionService _llm;
    private readonly DynamicSpawnService _spawn;
    private readonly RealtimeService _realtime;
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> TaskCancellation = new();

    public TaskService(IDbContextFactory<AppDbContext> factory, LLMExecutionService llm, DynamicSpawnService spawn, RealtimeService realtime)
    {
        _factory = factory;
        _llm = llm;
        _spawn = spawn;
        _realtime = realtime;
    }

    public async Task<List<AgentTask>> GetAllAsync(int count = 100)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Tasks
            .OrderByDescending(t => t.CreatedAt)
            .Take(count)
            .ToListAsync();
    }

    public async Task<List<AgentTask>> GetForAgentAsync(string agentId, int count = 50)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Tasks
            .Where(t => t.AgentId == agentId)
            .OrderByDescending(t => t.CreatedAt)
            .Take(count)
            .ToListAsync();
    }

    public async Task<AgentTask?> GetByIdAsync(string id)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Tasks.FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task<AgentTask> CreateAndRunAsync(string agentId, string taskName, string input,
        TaskPriority priority = TaskPriority.Medium, TriggerSource trigger = TriggerSource.Manual)
    {
        await using var db = _factory.CreateDbContext();

        var agent = await db.Agents
            .Include(a => a.LLMProvider)
            .Include(a => a.MCPServers).ThenInclude(m => m.MCPServer)
            .FirstOrDefaultAsync(a => a.Id == agentId)
                    ?? throw new InvalidOperationException("Agent not found");

        var task = new AgentTask
        {
            Id = $"t-{Guid.NewGuid():N}"[..12],
            AgentId = agentId,
            AgentName = agent.Name,
            Name = taskName,
            Description = taskName,
            TaskType = agent.Type.ToString(),
            Status = AgentTaskStatus.Running,
            Priority = priority,
            TriggerSource = trigger,
            Input = input,
            SystemPromptSnapshot = agent.SystemPrompt,
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            Progress = 0
        };

        // Check approval gate
        if (agent.RequiresApproval)
        {
            task.Status = AgentTaskStatus.PendingApproval;
            db.Tasks.Add(task);
            await db.SaveChangesAsync();
            await _realtime.TaskUpdatedAsync(task.Id, task.AgentId);
            return task;
        }

        db.Tasks.Add(task);
        agent.Status = AgentStatus.Active;
        agent.CurrentTask = taskName;
        await db.SaveChangesAsync();
        await _realtime.TaskUpdatedAsync(task.Id, task.AgentId);
        await _realtime.AgentUpdatedAsync(agent.Id);

        // Execute async (fire and update)
        var cts = new CancellationTokenSource();
        TaskCancellation[task.Id] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                if (agent.SpawnEnabled)
                    await _spawn.ExecuteWithSpawningAsync(task.Id, agent, cts.Token);
                else
                    await ExecuteTaskAsync(task.Id, agent, cts.Token);
            }
            finally
            {
                if (TaskCancellation.TryRemove(task.Id, out var source))
                    source.Dispose();
            }
        });

        return task;
    }

    public async Task ApproveTaskAsync(string taskId)
    {
        await using var db = _factory.CreateDbContext();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId);
        if (task == null || task.Status != AgentTaskStatus.PendingApproval) return;

        var agent = await db.Agents
            .Include(a => a.LLMProvider)
            .Include(a => a.MCPServers).ThenInclude(m => m.MCPServer)
            .FirstOrDefaultAsync(a => a.Id == task.AgentId);
        if (agent == null) return;

        task.Status = AgentTaskStatus.Running;
        task.StartedAt = DateTime.UtcNow;
        agent.Status = AgentStatus.Active;
        agent.CurrentTask = task.Name;
        await db.SaveChangesAsync();
        await _realtime.TaskUpdatedAsync(task.Id, task.AgentId);
        await _realtime.AgentUpdatedAsync(agent.Id);

        var cts = new CancellationTokenSource();
        TaskCancellation[task.Id] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                if (agent.SpawnEnabled)
                    await _spawn.ExecuteWithSpawningAsync(task.Id, agent, cts.Token);
                else
                    await ExecuteTaskAsync(task.Id, agent, cts.Token);
            }
            finally
            {
                if (TaskCancellation.TryRemove(task.Id, out var source))
                    source.Dispose();
            }
        });
    }

    public async Task CancelTaskAsync(string taskId)
    {
        await using var db = _factory.CreateDbContext();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId);
        if (task == null) return;
        task.Status = AgentTaskStatus.Cancelled;
        task.CompletedAt = DateTime.UtcNow;
        task.ErrorMessage ??= "Task cancelled.";

        var agent = await db.Agents.FindAsync(task.AgentId);
        if (agent != null && agent.CurrentTask == task.Name)
        {
            agent.Status = AgentStatus.Idle;
            agent.CurrentTask = "Idle";
        }

        await db.SaveChangesAsync();
        if (TaskCancellation.TryGetValue(taskId, out var cts))
            cts.Cancel();

        await _realtime.TaskUpdatedAsync(task.Id, task.AgentId);
        if (agent != null)
            await _realtime.AgentUpdatedAsync(agent.Id);
    }

    public async Task SubmitFeedbackAsync(string taskId, int rating, string? note, string? correctedOutput)
    {
        await using var db = _factory.CreateDbContext();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId);
        if (task == null) return;

        task.FeedbackRating = rating;
        task.FeedbackNote = note;

        db.TaskFeedbacks.Add(new TaskFeedback
        {
            TaskId = taskId,
            AgentId = task.AgentId,
            Type = rating >= 4 ? FeedbackType.ThumbsUp : FeedbackType.ThumbsDown,
            Rating = rating,
            Note = note,
            CorrectedOutput = correctedOutput
        });

        await db.SaveChangesAsync();
        await _realtime.TaskUpdatedAsync(task.Id, task.AgentId);
    }

    public async Task<AgentTask> RetryTaskAsync(string taskId)
    {
        await using var db = _factory.CreateDbContext();
        var originalTask = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId)
            ?? throw new InvalidOperationException("Task not found.");

        if (string.IsNullOrWhiteSpace(originalTask.Input))
            throw new InvalidOperationException("Cannot retry a task without saved input.");

        return await CreateAndRunAsync(
            originalTask.AgentId,
            originalTask.Name,
            originalTask.Input,
            originalTask.Priority,
            TriggerSource.Manual);
    }

    private async Task ExecuteTaskAsync(string taskId, Agent agent, CancellationToken cancellationToken)
    {
        await using var db = _factory.CreateDbContext();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId);
        if (task == null) return;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            task.Progress = 10;
            await db.SaveChangesAsync();
            await _realtime.TaskUpdatedAsync(task.Id, task.AgentId);

            var result = await _llm.ExecuteAsync(agent, task.Input ?? string.Empty, task.SystemPromptSnapshot ?? agent.SystemPrompt, cancellationToken);

            await db.Entry(task).ReloadAsync(cancellationToken);
            if (task.Status == AgentTaskStatus.Cancelled || cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);

            task.Output = result.Output;
            task.PromptTokens = result.PromptTokens;
            task.CompletionTokens = result.CompletionTokens;
            task.TotalTokens = result.TotalTokens;
            task.CostUSD = result.CostUSD;
            task.ModelUsed = result.ModelUsed;
            task.ExecutionTrace = result.Trace;
            task.ConfidenceScore = result.ConfidenceScore;
            task.Progress = 100;
            task.Status = AgentTaskStatus.Completed;
            task.CompletedAt = DateTime.UtcNow;

            // Update agent token totals
            var agentRecord = await db.Agents.FindAsync(agent.Id);
            if (agentRecord != null)
            {
                agentRecord.TotalTokensUsed += result.TotalTokens;
                agentRecord.TotalCostUSD += result.CostUSD;
                agentRecord.TasksCompleted++;
                agentRecord.Status = AgentStatus.Idle;
                agentRecord.CurrentTask = "Idle";
            }

            db.Logs.Add(new LogEntry
            {
                AgentId = agent.Id, AgentName = agent.Name, TaskId = taskId,
                Message = $"Task '{task.Name}' completed — {result.TotalTokens} tokens",
                Level = AgentLogLevel.Success, Timestamp = DateTime.UtcNow
            });
        }
        catch (OperationCanceledException)
        {
            task.Status = AgentTaskStatus.Cancelled;
            task.CompletedAt = DateTime.UtcNow;
            task.Progress = 0;
            task.ErrorMessage ??= "Task cancelled.";

            var agentRecord = await db.Agents.FindAsync(agent.Id);
            if (agentRecord != null)
            {
                agentRecord.Status = AgentStatus.Idle;
                agentRecord.CurrentTask = "Idle";
            }
        }
        catch (Exception ex)
        {
            task.Status = AgentTaskStatus.Failed;
            task.ErrorMessage = ex.Message;
            task.CompletedAt = DateTime.UtcNow;
            task.Progress = 0;

            var agentRecord = await db.Agents.FindAsync(agent.Id);
            if (agentRecord != null)
            {
                agentRecord.TasksFailed++;
                agentRecord.Status = AgentStatus.Error;
            }

            db.Logs.Add(new LogEntry
            {
                AgentId = agent.Id, AgentName = agent.Name, TaskId = taskId,
                Message = $"Task '{task.Name}' failed: {ex.Message}",
                Level = AgentLogLevel.Error, Timestamp = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
        await _realtime.TaskUpdatedAsync(task.Id, task.AgentId);
        await _realtime.AgentUpdatedAsync(agent.Id);
        await _realtime.DashboardRefreshAsync();
    }
}
