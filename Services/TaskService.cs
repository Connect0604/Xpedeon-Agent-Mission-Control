using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class TaskService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly LLMExecutionService _llm;
    private readonly HermesOpenClawExecutionService _hermes;
    private readonly DynamicSpawnService _spawn;
    private readonly RealtimeService _realtime;
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> TaskCancellation = new();

    public TaskService(
        IDbContextFactory<AppDbContext> factory,
        LLMExecutionService llm,
        HermesOpenClawExecutionService hermes,
        DynamicSpawnService spawn,
        RealtimeService realtime)
    {
        _factory = factory;
        _llm = llm;
        _hermes = hermes;
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
        => await CreateAndRunInternalAsync(agentId, taskName, input, priority, trigger, null, null);

    private async Task<AgentTask> CreateAndRunInternalAsync(
        string agentId,
        string taskName,
        string input,
        TaskPriority priority,
        TriggerSource trigger,
        TaskExecutionProfile? executionProfile,
        string? replayOfTaskId)
    {
        await using var db = _factory.CreateDbContext();

        var persistedAgent = await db.Agents
            .Include(a => a.LLMProvider)
            .Include(a => a.MCPServers).ThenInclude(m => m.MCPServer)
            .Include(a => a.Skills).ThenInclude(s => s.SkillDefinition)
            .FirstOrDefaultAsync(a => a.Id == agentId)
                    ?? throw new InvalidOperationException("Agent not found");

        var executionAgent = executionProfile?.RuntimeAgent ?? persistedAgent;
        var effectiveSkills = executionAgent.Skills
            .Where(s => s.IsEnabled && s.SkillDefinition is { IsEnabled: true })
            .ToList();
        var effectiveProvider = executionAgent.LLMProvider;
        var effectiveMcpServers = executionAgent.MCPServers
            .Where(m => m.IsEnabled && m.MCPServer is { IsEnabled: true })
            .ToList();

        var task = new AgentTask
        {
            Id = $"t-{Guid.NewGuid():N}"[..12],
            AgentId = agentId,
            AgentName = persistedAgent.Name,
            Name = taskName,
            Description = taskName,
            TaskType = persistedAgent.Type.ToString(),
            Status = AgentTaskStatus.Running,
            Priority = priority,
            TriggerSource = trigger,
            Input = input,
            SystemPromptSnapshot = executionAgent.SystemPrompt,
            ProviderSnapshotId = effectiveProvider?.Id,
            ProviderSnapshotName = effectiveProvider?.Name,
            ProviderSnapshotModel = effectiveProvider?.ModelName,
            SkillSnapshotJson = SerializeSnapshot(effectiveSkills.Select(s => new SnapshotItem(s.SkillDefinitionId, s.SkillDefinition?.Name ?? s.SkillDefinitionId))),
            MCPServerSnapshotJson = SerializeSnapshot(effectiveMcpServers.Select(s => new SnapshotItem(s.MCPServerId, s.MCPServer?.Name ?? s.MCPServerId))),
            ApprovalEvidence = BuildApprovalEvidence(executionAgent, input, effectiveSkills, effectiveMcpServers),
            ExecutionBackendSnapshot = executionAgent.ExecutionBackend,
            ReplayOfTaskId = replayOfTaskId,
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            Progress = 0
        };

        // Check approval gate
        if (persistedAgent.RequiresApproval || effectiveSkills.Any(s => s.SkillDefinition?.RequiresApproval == true))
        {
            task.Status = AgentTaskStatus.PendingApproval;
            db.Tasks.Add(task);
            await db.SaveChangesAsync();
            await _realtime.TaskUpdatedAsync(task.Id, task.AgentId);
            return task;
        }

        db.Tasks.Add(task);
        persistedAgent.Status = AgentStatus.Active;
        persistedAgent.CurrentTask = taskName;
        await db.SaveChangesAsync();
        await _realtime.TaskUpdatedAsync(task.Id, task.AgentId);
        await _realtime.AgentUpdatedAsync(persistedAgent.Id);

        // Execute async (fire and update)
        var cts = new CancellationTokenSource();
        TaskCancellation[task.Id] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                if (executionAgent.ExecutionBackend == ExecutionBackend.HermesOpenClaw)
                    await DispatchToHermesAsync(task.Id, executionAgent, cts.Token);
                else if (executionAgent.SpawnEnabled)
                    await _spawn.ExecuteWithSpawningAsync(task.Id, executionAgent, cts.Token);
                else
                    await ExecuteTaskAsync(task.Id, executionAgent, cts.Token);
            }
            finally
            {
                if (TaskCancellation.TryRemove(task.Id, out var source))
                    source.Dispose();
            }
        });

        return task;
    }

    public async Task ApproveTaskAsync(string taskId, string? approvalComment = null)
    {
        await using var db = _factory.CreateDbContext();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId);
        if (task == null || task.Status != AgentTaskStatus.PendingApproval) return;

        var agent = await db.Agents
            .Include(a => a.LLMProvider)
            .Include(a => a.MCPServers).ThenInclude(m => m.MCPServer)
            .Include(a => a.Skills).ThenInclude(s => s.SkillDefinition)
            .FirstOrDefaultAsync(a => a.Id == task.AgentId);
        if (agent == null) return;

        task.Status = AgentTaskStatus.Running;
        task.StartedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(approvalComment))
            task.ApprovalComment = approvalComment;
        agent.Status = AgentStatus.Active;
        agent.CurrentTask = task.Name;
        await db.SaveChangesAsync();
        await _realtime.TaskUpdatedAsync(task.Id, task.AgentId);
        await _realtime.AgentUpdatedAsync(agent.Id);

        var executionAgent = await BuildExecutionAgentFromTaskSnapshotAsync(agent, task);

        var cts = new CancellationTokenSource();
        TaskCancellation[task.Id] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                if (executionAgent.ExecutionBackend == ExecutionBackend.HermesOpenClaw)
                    await DispatchToHermesAsync(task.Id, executionAgent, cts.Token);
                else if (executionAgent.SpawnEnabled)
                    await _spawn.ExecuteWithSpawningAsync(task.Id, executionAgent, cts.Token);
                else
                    await ExecuteTaskAsync(task.Id, executionAgent, cts.Token);
            }
            finally
            {
                if (TaskCancellation.TryRemove(task.Id, out var source))
                    source.Dispose();
            }
        });
    }

    public async Task CancelTaskAsync(string taskId, string? approvalComment = null)
    {
        await using var db = _factory.CreateDbContext();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId);
        if (task == null) return;
        task.Status = AgentTaskStatus.Cancelled;
        task.CompletedAt = DateTime.UtcNow;
        task.ErrorMessage ??= "Task cancelled.";
        if (!string.IsNullOrWhiteSpace(approvalComment))
            task.ApprovalComment = approvalComment;

        var agent = await db.Agents.FindAsync(task.AgentId);
        if (agent != null && agent.CurrentTask == task.Name)
        {
            agent.Status = AgentStatus.Idle;
            agent.CurrentTask = "Idle";
        }

        await db.SaveChangesAsync();
        if (TaskCancellation.TryGetValue(taskId, out var cts))
            cts.Cancel();

        if (!string.IsNullOrWhiteSpace(task.ExternalRunId) && task.ExecutionBackendSnapshot == ExecutionBackend.HermesOpenClaw)
        {
            try
            {
                await _hermes.CancelRunAsync(task.ExternalRunId);
                task.ExternalStatus = ExternalRunStatus.Cancelled;
                task.ExternalLastSyncedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                task.ExternalError = ex.Message;
                await db.SaveChangesAsync();
            }
        }

        await _realtime.TaskUpdatedAsync(task.Id, task.AgentId);
        if (agent != null)
            await _realtime.AgentUpdatedAsync(agent.Id);
    }

    public async Task DeleteTaskAsync(string taskId)
    {
        await using var db = _factory.CreateDbContext();

        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId);
        if (task == null)
            return;

        if (TaskCancellation.TryGetValue(taskId, out var cts))
            cts.Cancel();

        if (!string.IsNullOrWhiteSpace(task.ExternalRunId) && task.ExecutionBackendSnapshot == ExecutionBackend.HermesOpenClaw)
        {
            try
            {
                await _hermes.CancelRunAsync(task.ExternalRunId);
            }
            catch
            {
                // Best-effort cleanup for remote execution before deleting the local record.
            }
        }

        var feedbacks = await db.TaskFeedbacks.Where(f => f.TaskId == taskId).ToListAsync();
        if (feedbacks.Any())
            db.TaskFeedbacks.RemoveRange(feedbacks);

        var logs = await db.Logs.Where(l => l.TaskId == taskId).ToListAsync();
        if (logs.Any())
            db.Logs.RemoveRange(logs);

        var agent = await db.Agents.FindAsync(task.AgentId);
        if (agent != null && agent.CurrentTask == task.Name)
        {
            agent.Status = AgentStatus.Idle;
            agent.CurrentTask = "Idle";
        }

        db.Tasks.Remove(task);
        await db.SaveChangesAsync();

        await _realtime.TaskUpdatedAsync(taskId, task.AgentId);
        await _realtime.DashboardRefreshAsync();
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

    public async Task<AgentTask> ReplayTaskAsync(string taskId)
    {
        await using var db = _factory.CreateDbContext();
        var originalTask = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId)
            ?? throw new InvalidOperationException("Task not found.");

        if (string.IsNullOrWhiteSpace(originalTask.Input))
            throw new InvalidOperationException("Cannot replay a task without saved input.");

        var agent = await db.Agents
            .Include(a => a.LLMProvider)
            .Include(a => a.MCPServers).ThenInclude(m => m.MCPServer)
            .Include(a => a.Skills).ThenInclude(s => s.SkillDefinition)
            .FirstOrDefaultAsync(a => a.Id == originalTask.AgentId)
            ?? throw new InvalidOperationException("Owning agent not found.");

        var replayAgent = await BuildExecutionAgentFromTaskSnapshotAsync(agent, originalTask);

        return await CreateAndRunInternalAsync(
            originalTask.AgentId,
            $"{originalTask.Name} (Replay)",
            originalTask.Input,
            originalTask.Priority,
            TriggerSource.Manual,
            new TaskExecutionProfile { RuntimeAgent = replayAgent },
            originalTask.Id);
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
            task.ToolCallsUsed = result.ToolCallsUsed;
            task.Progress = 100;
            task.Status = AgentTaskStatus.Completed;
            task.CompletedAt = DateTime.UtcNow;
            task.DurationMs = task.StartedAt.HasValue
                ? (long)Math.Max(0, (task.CompletedAt.Value - task.StartedAt.Value).TotalMilliseconds)
                : 0;

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
            task.DurationMs = task.StartedAt.HasValue
                ? (long)Math.Max(0, (task.CompletedAt.Value - task.StartedAt.Value).TotalMilliseconds)
                : 0;

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
            task.DurationMs = task.StartedAt.HasValue
                ? (long)Math.Max(0, (task.CompletedAt.Value - task.StartedAt.Value).TotalMilliseconds)
                : 0;

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

    private async Task DispatchToHermesAsync(string taskId, Agent agent, CancellationToken cancellationToken)
    {
        await using var db = _factory.CreateDbContext();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);
        if (task == null)
            return;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var submit = await _hermes.SubmitTaskAsync(agent, task, cancellationToken);

            task.ExternalRunId = submit.ExternalRunId;
            task.ExternalBackend = agent.ExecutionBackend.ToString();
            task.ExternalStatus = submit.Status;
            task.ExternalSubmittedAt = DateTime.UtcNow;
            task.ExternalLastSyncedAt = DateTime.UtcNow;
            task.ExternalTrace = submit.Trace;
            task.ExternalResultJson = submit.RawResponse;
            task.ExecutionTrace = string.Join(Environment.NewLine, new[]
            {
                task.ExecutionTrace,
                $"Delegated to Hermes/OpenClaw run {submit.ExternalRunId}",
                submit.Trace
            }.Where(x => !string.IsNullOrWhiteSpace(x)));
            task.Progress = 20;

            db.Logs.Add(new LogEntry
            {
                AgentId = agent.Id,
                AgentName = agent.Name,
                TaskId = task.Id,
                Message = $"Delegated task '{task.Name}' to Hermes/OpenClaw as run {submit.ExternalRunId}.",
                Level = AgentLogLevel.Info,
                Timestamp = DateTime.UtcNow
            });

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            task.Status = AgentTaskStatus.Cancelled;
            task.ExternalStatus = ExternalRunStatus.Cancelled;
            task.ErrorMessage ??= "Task cancelled before Hermes/OpenClaw submission completed.";
            task.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            task.Status = AgentTaskStatus.Failed;
            task.ExternalStatus = ExternalRunStatus.Failed;
            task.ExternalError = ex.Message;
            task.ErrorMessage = ex.Message;
            task.CompletedAt = DateTime.UtcNow;
            task.Progress = 0;

            var agentRecord = await db.Agents.FindAsync(new object?[] { agent.Id }, cancellationToken);
            if (agentRecord != null)
            {
                agentRecord.TasksFailed++;
                agentRecord.Status = AgentStatus.Error;
                agentRecord.CurrentTask = "Idle";
            }

            db.Logs.Add(new LogEntry
            {
                AgentId = agent.Id,
                AgentName = agent.Name,
                TaskId = task.Id,
                Message = $"Delegated task '{task.Name}' failed during submission: {ex.Message}",
                Level = AgentLogLevel.Error,
                Timestamp = DateTime.UtcNow
            });

            await db.SaveChangesAsync(cancellationToken);
        }

        await _realtime.TaskUpdatedAsync(task.Id, task.AgentId);
        await _realtime.AgentUpdatedAsync(agent.Id);
        await _realtime.DashboardRefreshAsync();
    }

    private async Task<Agent> BuildExecutionAgentFromTaskSnapshotAsync(Agent persistedAgent, AgentTask task)
    {
        await using var db = _factory.CreateDbContext();

        var provider = !string.IsNullOrWhiteSpace(task.ProviderSnapshotId)
            ? await db.LLMProviders.FirstOrDefaultAsync(p => p.Id == task.ProviderSnapshotId)
            : persistedAgent.LLMProvider;

        var skillIds = DeserializeSnapshotIds(task.SkillSnapshotJson);
        var mcpIds = DeserializeSnapshotIds(task.MCPServerSnapshotJson);

        var skills = skillIds.Any()
            ? await db.AgentSkillAssignments
                .Where(s => s.AgentId == persistedAgent.Id && skillIds.Contains(s.SkillDefinitionId))
                .Include(s => s.SkillDefinition)
                .ToListAsync()
            : persistedAgent.Skills;

        var mcpAssignments = mcpIds.Any()
            ? await db.AgentMCPServers
                .Where(m => m.AgentId == persistedAgent.Id && mcpIds.Contains(m.MCPServerId))
                .Include(m => m.MCPServer)
                .ToListAsync()
            : persistedAgent.MCPServers;

        return new Agent
        {
            Id = persistedAgent.Id,
            Name = persistedAgent.Name,
            Type = persistedAgent.Type,
            Description = persistedAgent.Description,
            LLMProviderId = provider?.Id,
            LLMProvider = provider,
            SystemPrompt = task.SystemPromptSnapshot ?? persistedAgent.SystemPrompt,
            ExecutionBackend = task.ExecutionBackendSnapshot,
            RequiresApproval = persistedAgent.RequiresApproval,
            ConfidenceThreshold = persistedAgent.ConfidenceThreshold,
            SpawnEnabled = persistedAgent.SpawnEnabled,
            SpawnMode = persistedAgent.SpawnMode,
            SpawnTriggerType = persistedAgent.SpawnTriggerType,
            SpawnTrigger = persistedAgent.SpawnTrigger,
            MaxSpawns = persistedAgent.MaxSpawns,
            MaxDepth = persistedAgent.MaxDepth,
            ChildPromptTemplate = persistedAgent.ChildPromptTemplate,
            SpawnLifecycle = persistedAgent.SpawnLifecycle,
            SpawnAggregation = persistedAgent.SpawnAggregation,
            MCPServers = mcpAssignments,
            Skills = skills
        };
    }

    private static string BuildApprovalEvidence(
        Agent executionAgent,
        string input,
        IReadOnlyCollection<AgentSkillAssignment> skills,
        IReadOnlyCollection<AgentMCPServer> mcpServers)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Pending execution review");
        sb.AppendLine($"Execution backend: {executionAgent.ExecutionBackend}");
        sb.AppendLine($"Provider: {executionAgent.LLMProvider?.Name ?? "Unassigned"}");
        sb.AppendLine($"Model: {executionAgent.LLMProvider?.ModelName ?? "Unassigned"}");
        sb.AppendLine($"Prompt size: ~{(string.IsNullOrWhiteSpace(executionAgent.SystemPrompt) ? 0 : (int)(executionAgent.SystemPrompt.Length / 3.8))} tokens");
        sb.AppendLine($"Input preview: {(string.IsNullOrWhiteSpace(input) ? "(empty)" : input[..Math.Min(160, input.Length)])}{(input.Length > 160 ? "..." : "")}");

        if (skills.Any())
        {
            sb.AppendLine("Skills:");
            foreach (var skill in skills.Where(s => s.SkillDefinition != null))
            {
                sb.AppendLine($"- {skill.SkillDefinition!.Name}");
            }
        }

        if (mcpServers.Any())
        {
            sb.AppendLine("Attached MCP servers:");
            foreach (var mcp in mcpServers.Where(m => m.MCPServer != null))
            {
                sb.AppendLine($"- {mcp.MCPServer!.Name}");
            }
        }

        sb.AppendLine("Tool execution has not started yet. Approval gates this run before any MCP calls occur.");
        return sb.ToString().Trim();
    }

    private static string SerializeSnapshot(IEnumerable<SnapshotItem> items)
        => JsonSerializer.Serialize(items.ToList());

    private static List<string> DeserializeSnapshotIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<string>();

        try
        {
            return (JsonSerializer.Deserialize<List<SnapshotItem>>(json) ?? new List<SnapshotItem>())
                .Where(i => !string.IsNullOrWhiteSpace(i.Id))
                .Select(i => i.Id)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private sealed class TaskExecutionProfile
    {
        public Agent RuntimeAgent { get; set; } = new();
    }

    private sealed class SnapshotItem
    {
        public SnapshotItem() { }

        public SnapshotItem(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }
}
