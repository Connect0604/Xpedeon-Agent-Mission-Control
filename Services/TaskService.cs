using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
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
    private readonly LocalAutomationOrchestrator _localAutomation;
    private readonly LocalAutomationResponseFormatter _localAutomationFormatter;
    private readonly AgentMemoryCaptureService _memoryCapture;
    private readonly RealtimeService _realtime;
    private readonly TaskExecutionEventService _events;
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> TaskCancellation = new();

    public TaskService(
        IDbContextFactory<AppDbContext> factory,
        LLMExecutionService llm,
        HermesOpenClawExecutionService hermes,
        DynamicSpawnService spawn,
        LocalAutomationOrchestrator localAutomation,
        LocalAutomationResponseFormatter localAutomationFormatter,
        AgentMemoryCaptureService memoryCapture,
        RealtimeService realtime,
        TaskExecutionEventService events)
    {
        _factory = factory;
        _llm = llm;
        _hermes = hermes;
        _spawn = spawn;
        _localAutomation = localAutomation;
        _localAutomationFormatter = localAutomationFormatter;
        _memoryCapture = memoryCapture;
        _realtime = realtime;
        _events = events;
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

    public async Task<List<AgentTask>> GetRecentOutputsAsync(int count = 200)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Tasks
            .Where(HasOutputOrErrorExpression())
            .OrderByDescending(t => t.CompletedAt ?? t.CreatedAt)
            .ThenByDescending(t => t.CreatedAt)
            .Take(count)
            .ToListAsync();
    }

    public async Task<List<AgentTask>> GetLatestOutputPerAgentAsync(int count = 200)
    {
        await using var db = _factory.CreateDbContext();
        var outputTasks = await db.Tasks
            .Where(HasOutputOrErrorExpression())
            .OrderByDescending(t => t.CompletedAt ?? t.CreatedAt)
            .ThenByDescending(t => t.CreatedAt)
            .ToListAsync();

        return outputTasks
            .GroupBy(t => t.AgentId)
            .Select(g => g.First())
            .Take(count)
            .ToList();
    }

    public async Task<List<AgentTask>> GetRecentFailedOutputsAsync(int count = 200)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Tasks
            .Where(t =>
                t.Status == AgentTaskStatus.Failed ||
                t.Status == AgentTaskStatus.Cancelled)
            .Where(HasOutputOrErrorExpression())
            .OrderByDescending(t => t.CompletedAt ?? t.CreatedAt)
            .ThenByDescending(t => t.CreatedAt)
            .Take(count)
            .ToListAsync();
    }

    public async Task<List<AgentTask>> GetOutputsForAgentAsync(string agentId, int count = 200)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Tasks
            .Where(t => t.AgentId == agentId)
            .Where(HasOutputOrErrorExpression())
            .OrderByDescending(t => t.CompletedAt ?? t.CreatedAt)
            .ThenByDescending(t => t.CreatedAt)
            .Take(count)
            .ToListAsync();
    }

    public async Task<AgentTask?> GetByIdAsync(string id)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Tasks.FirstOrDefaultAsync(t => t.Id == id);
    }

    private static System.Linq.Expressions.Expression<Func<AgentTask, bool>> HasOutputOrErrorExpression()
        => t => (t.Output != null && t.Output != "") || (t.ErrorMessage != null && t.ErrorMessage != "");

    public async Task<AgentTask> CreateAndRunAsync(string agentId, string taskName, string input,
        TaskPriority priority = TaskPriority.Medium, TriggerSource trigger = TriggerSource.Manual)
        => await CreateAndRunInternalAsync(agentId, taskName, input, priority, trigger, null, null, null);

    public async Task<AgentTask> CreateAndRunForWorkflowAsync(
        string agentId,
        string taskName,
        string input,
        TaskPriority priority,
        TriggerSource trigger,
        WorkflowTaskContext workflowContext,
        string? promptOverride = null)
    {
        TaskExecutionProfile? executionProfile = null;

        if (!string.IsNullOrWhiteSpace(promptOverride))
        {
            executionProfile = new TaskExecutionProfile
            {
                RuntimeAgent = await BuildExecutionAgentForPromptOverrideAsync(agentId, promptOverride)
            };
        }

        return await CreateAndRunInternalAsync(
            agentId,
            taskName,
            input,
            priority,
            trigger,
            executionProfile,
            null,
            workflowContext);
    }

    private async Task<AgentTask> CreateAndRunInternalAsync(
        string agentId,
        string taskName,
        string input,
        TaskPriority priority,
        TriggerSource trigger,
        TaskExecutionProfile? executionProfile,
        string? replayOfTaskId,
        WorkflowTaskContext? workflowContext)
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
            WorkflowRunId = workflowContext?.WorkflowRunId,
            WorkflowStepId = workflowContext?.WorkflowStepId,
            WorkflowStepRunId = workflowContext?.WorkflowStepRunId,
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
            await _events.LogAsync(
                task.Id,
                task.AgentId,
                TaskExecutionEventType.TaskPendingApproval,
                "Task created and is waiting for approval.",
                new
                {
                    task.Name,
                    Provider = task.ProviderSnapshotName,
                    Model = task.ProviderSnapshotModel,
                    Skills = effectiveSkills.Select(s => s.SkillDefinition?.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList(),
                    MCPServers = effectiveMcpServers.Select(m => m.MCPServer?.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList()
                });
            await _realtime.TaskUpdatedAsync(task.Id, task.AgentId);
            return task;
        }

        db.Tasks.Add(task);
        persistedAgent.Status = AgentStatus.Active;
        persistedAgent.CurrentTask = taskName;
        await db.SaveChangesAsync();
        await _events.LogAsync(
            task.Id,
            task.AgentId,
            TaskExecutionEventType.TaskCreated,
            "Task created and queued for execution.",
            new
            {
                task.Name,
                Provider = task.ProviderSnapshotName,
                Model = task.ProviderSnapshotModel,
                Skills = effectiveSkills.Select(s => s.SkillDefinition?.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList(),
                MCPServers = effectiveMcpServers.Select(m => m.MCPServer?.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList(),
                task.ExecutionBackendSnapshot
            });
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
        await _events.LogAsync(
            task.Id,
            task.AgentId,
            TaskExecutionEventType.TaskApproved,
            "Task approved and execution started.",
            new { task.Name, approvalComment });
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
                    await ExecuteTaskAsync(task.Id, executionAgent, cts.Token, skipApproval: true);
            }
            finally
            {
                if (TaskCancellation.TryRemove(task.Id, out var source))
                    source.Dispose();
            }
        });
    }

    public async Task CancelTaskAsync(string taskId, string? approvalComment = null)
        => await StopTaskInternalAsync(taskId, approvalComment, "Task cancelled.", false);

    public async Task ForceStopTaskAsync(string taskId, string? operatorComment = null)
        => await StopTaskInternalAsync(taskId, operatorComment, "Task force-stopped by operator.", true);

    private async Task StopTaskInternalAsync(string taskId, string? comment, string defaultErrorMessage, bool isForceStop)
    {
        await using var db = _factory.CreateDbContext();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId);
        if (task == null) return;
        if (task.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled) return;

        task.Status = AgentTaskStatus.Cancelled;
        task.CompletedAt = DateTime.UtcNow;
        task.ErrorMessage ??= defaultErrorMessage;
        if (!string.IsNullOrWhiteSpace(comment))
            task.ApprovalComment = comment;

        var agent = await db.Agents.FindAsync(task.AgentId);
        if (agent != null && agent.CurrentTask == task.Name)
        {
            agent.Status = AgentStatus.Idle;
            agent.CurrentTask = "Idle";
        }

        db.Logs.Add(new LogEntry
        {
            AgentId = task.AgentId,
            AgentName = task.AgentName,
            TaskId = task.Id,
            Message = isForceStop
                ? $"Task '{task.Name}' was force-stopped by an operator."
                : $"Task '{task.Name}' was cancelled.",
            Level = isForceStop ? AgentLogLevel.Warning : AgentLogLevel.Info,
            Timestamp = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
        await _events.LogAsync(
            task.Id,
            task.AgentId,
            TaskExecutionEventType.TaskCancelled,
            isForceStop ? "Task force-stopped by operator." : "Task cancelled.",
            new { comment, isForceStop });
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

        _realtime.NotifyTaskCompleted(new TaskCompletedNotification(
            task.Id, task.AgentId, task.AgentName, task.Name, task.Status));
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
            originalTask.Id,
            null);
    }

    private async Task<Agent> BuildExecutionAgentForPromptOverrideAsync(string agentId, string promptOverride)
    {
        await using var db = _factory.CreateDbContext();
        var persistedAgent = await db.Agents
            .Include(a => a.LLMProvider)
            .Include(a => a.MCPServers).ThenInclude(m => m.MCPServer)
            .Include(a => a.Skills).ThenInclude(s => s.SkillDefinition)
            .FirstOrDefaultAsync(a => a.Id == agentId)
                ?? throw new InvalidOperationException("Agent not found");

        return new Agent
        {
            Id = persistedAgent.Id,
            Name = persistedAgent.Name,
            Type = persistedAgent.Type,
            Description = persistedAgent.Description,
            LLMProviderId = persistedAgent.LLMProviderId,
            LLMProvider = persistedAgent.LLMProvider,
            SystemPrompt = promptOverride,
            ExecutionBackend = persistedAgent.ExecutionBackend,
            RequiresApproval = persistedAgent.RequiresApproval,
            ConfidenceThreshold = persistedAgent.ConfidenceThreshold,
            LocalAutomationEnabled = persistedAgent.LocalAutomationEnabled,
            AllowPowerShellScripts = persistedAgent.AllowPowerShellScripts,
            AllowDestructiveActions = persistedAgent.AllowDestructiveActions,
            LocalAutomationApprovalMode = persistedAgent.LocalAutomationApprovalMode,
            AllowedLocalRootsJson = persistedAgent.AllowedLocalRootsJson,
            SpawnEnabled = persistedAgent.SpawnEnabled,
            SpawnMode = persistedAgent.SpawnMode,
            SpawnTriggerType = persistedAgent.SpawnTriggerType,
            SpawnTrigger = persistedAgent.SpawnTrigger,
            MaxSpawns = persistedAgent.MaxSpawns,
            MaxDepth = persistedAgent.MaxDepth,
            ChildPromptTemplate = persistedAgent.ChildPromptTemplate,
            SpawnLifecycle = persistedAgent.SpawnLifecycle,
            SpawnAggregation = persistedAgent.SpawnAggregation,
            MCPServers = persistedAgent.MCPServers,
            Skills = persistedAgent.Skills
        };
    }

    private async Task ExecuteTaskAsync(string taskId, Agent agent, CancellationToken cancellationToken, bool skipApproval = false)
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

            if (agent.LocalAutomationEnabled)
            {
                var planningContext = await BuildLocalAutomationPlanningContextAsync(db, task);
                var shortcutPlan = TryBuildRecentFolderShortcut(task.Input ?? string.Empty, planningContext.RecentTouchedPaths, agent.AllowedLocalRootsJson);
                var localResult = shortcutPlan is not null
                    ? await _localAutomation.ExecuteProvidedPlanAsync(agent, task, shortcutPlan, cancellationToken, skipApproval)
                    : await _localAutomation.PlanAndExecuteAsync(
                        agent,
                        task,
                        cancellationToken,
                        skipApproval,
                        planningContext.Input,
                        planningContext.RecentTouchedPaths);

                if (localResult.RequiresElevatedApproval)
                {
                    task.RequiresElevatedApproval = true;
                    task.Status = AgentTaskStatus.PendingApproval;
                    task.Progress = 0;
                    task.LocalActionResultJson = JsonSerializer.Serialize(localResult);
                    task.LocalCapabilityId = localResult.LocalCapabilityId;
                    task.TouchedPathsJson = JsonSerializer.Serialize(localResult.TouchedPaths);
                    task.ApprovalEvidence = localResult.Message;
                    task.PromptTokens = localResult.PromptTokens;
                    task.CompletionTokens = localResult.CompletionTokens;
                    task.TotalTokens = localResult.TotalTokens;
                    task.CostUSD = localResult.CostUSD;
                    task.ModelUsed = localResult.ModelUsed;
                    task.ConfidenceScore = localResult.ConfidenceScore;

                    var pausedAgent = await db.Agents.FindAsync(agent.Id);
                    if (pausedAgent != null)
                    {
                        pausedAgent.Status = AgentStatus.Idle;
                        pausedAgent.CurrentTask = "Idle";
                    }

                    await _events.LogAsync(
                        task.Id,
                        task.AgentId,
                        TaskExecutionEventType.TaskPendingApproval,
                        "Task requires approval before local automation can execute.",
                        new
                        {
                            localResult.Plan?.Summary,
                            localResult.TouchedPaths
                        });

                    await db.SaveChangesAsync();
                    await _realtime.TaskUpdatedAsync(task.Id, task.AgentId);
                    await _realtime.AgentUpdatedAsync(agent.Id);
                    return;
                }

                if (!localResult.Success)
                {
                    task.Output = _localAutomationFormatter.FormatFailure(localResult);
                    task.LocalCapabilityId = localResult.LocalCapabilityId;
                    task.LocalCapabilityExecutionJson = localResult.LocalCapabilityExecutionJson;
                    task.LocalActionResultJson = JsonSerializer.Serialize(localResult);
                    task.TouchedPathsJson = JsonSerializer.Serialize(localResult.TouchedPaths);
                    task.PromptTokens = localResult.PromptTokens;
                    task.CompletionTokens = localResult.CompletionTokens;
                    task.TotalTokens = localResult.TotalTokens;
                    task.CostUSD = localResult.CostUSD;
                    task.ModelUsed = localResult.ModelUsed;
                    task.ConfidenceScore = localResult.ConfidenceScore;
                    task.ExecutionTrace = string.Join(Environment.NewLine, new[] { task.ExecutionTrace, localResult.Trace }.Where(x => !string.IsNullOrWhiteSpace(x)));
                    task.Status = AgentTaskStatus.Failed;
                    task.ErrorMessage = localResult.Message ?? "Local execution reported an unsuccessful result.";
                    task.CompletedAt = DateTime.UtcNow;
                    task.Progress = 0;
                    task.DurationMs = task.StartedAt.HasValue
                        ? (long)Math.Max(0, (task.CompletedAt.Value - task.StartedAt.Value).TotalMilliseconds)
                        : 0;

                    var failedAgentRecord = await db.Agents.FindAsync(agent.Id);
                    if (failedAgentRecord != null)
                    {
                        failedAgentRecord.TasksFailed++;
                        failedAgentRecord.Status = AgentStatus.Error;
                    }

                    db.Logs.Add(new LogEntry
                    {
                        AgentId = agent.Id,
                        AgentName = agent.Name,
                        TaskId = taskId,
                        Message = $"Task '{task.Name}' reported unsuccessful local execution: {task.ErrorMessage}",
                        Level = AgentLogLevel.Error,
                        Timestamp = DateTime.UtcNow
                    });
                    await _events.LogAsync(
                        task.Id,
                        task.AgentId,
                        TaskExecutionEventType.TaskFailed,
                        "Task reported unsuccessful local execution.",
                        new
                        {
                            localResult.LocalCapabilityId,
                            task.ErrorMessage,
                            task.DurationMs
                        });

                    await db.SaveChangesAsync();
                    _realtime.NotifyTaskCompleted(new TaskCompletedNotification(
                        task.Id, task.AgentId, task.AgentName, task.Name, task.Status));
                    await _realtime.TaskUpdatedAsync(task.Id, task.AgentId);
                    await _realtime.AgentUpdatedAsync(agent.Id);
                    await _realtime.DashboardRefreshAsync();
                    return;
                }

                task.Output = _localAutomationFormatter.FormatSuccess(localResult);
                task.LocalCapabilityId = localResult.LocalCapabilityId;
                task.LocalCapabilityExecutionJson = localResult.LocalCapabilityExecutionJson;
                task.LocalActionResultJson = JsonSerializer.Serialize(localResult);
                task.TouchedPathsJson = JsonSerializer.Serialize(localResult.TouchedPaths);
                task.PromptTokens = localResult.PromptTokens;
                task.CompletionTokens = localResult.CompletionTokens;
                task.TotalTokens = localResult.TotalTokens;
                task.CostUSD = localResult.CostUSD;
                task.ModelUsed = localResult.ModelUsed;
                task.ConfidenceScore = localResult.ConfidenceScore;
                task.ExecutionTrace = string.Join(Environment.NewLine, new[] { task.ExecutionTrace, localResult.Trace }.Where(x => !string.IsNullOrWhiteSpace(x)));
                task.Progress = 100;
                task.Status = AgentTaskStatus.Completed;
                task.CompletedAt = DateTime.UtcNow;
                task.DurationMs = task.StartedAt.HasValue
                    ? (long)Math.Max(0, (task.CompletedAt.Value - task.StartedAt.Value).TotalMilliseconds)
                    : 0;

                var localAgentRecord = await db.Agents.FindAsync(agent.Id);
                if (localAgentRecord != null)
                {
                    localAgentRecord.TotalTokensUsed += localResult.TotalTokens;
                    localAgentRecord.TotalCostUSD += localResult.CostUSD;
                    localAgentRecord.TasksCompleted++;
                    localAgentRecord.Status = AgentStatus.Idle;
                    localAgentRecord.CurrentTask = "Idle";
                }

                db.Logs.Add(new LogEntry
                {
                    AgentId = agent.Id,
                    AgentName = agent.Name,
                    TaskId = taskId,
                    Message = localResult.LocalCapabilityId is not null
                        ? $"Task '{task.Name}' completed local capability '{localResult.LocalCapabilityId}'."
                        : $"Task '{task.Name}' completed local automation with {localResult.ActionResults.Count} action(s)",
                    Level = AgentLogLevel.Success,
                    Timestamp = DateTime.UtcNow
                });
                await _events.LogAsync(
                    task.Id,
                    task.AgentId,
                    TaskExecutionEventType.TaskCompleted,
                    localResult.LocalCapabilityId is not null
                        ? "Task completed local capability successfully."
                        : "Task completed local automation successfully.",
                    new
                    {
                        localResult.TotalTokens,
                        localResult.CostUSD,
                        localResult.ModelUsed,
                        localResult.LocalCapabilityId,
                        ActionCount = localResult.ActionResults.Count,
                        task.DurationMs
                    });

                await db.SaveChangesAsync();
                await TryCaptureCompletedTaskMemoriesAsync(localAgentRecord ?? agent, task);
                _realtime.NotifyTaskCompleted(new TaskCompletedNotification(
                    task.Id, task.AgentId, task.AgentName, task.Name, task.Status));
                await _realtime.TaskUpdatedAsync(task.Id, task.AgentId);
                await _realtime.AgentUpdatedAsync(agent.Id);
                await _realtime.DashboardRefreshAsync();
                return;
            }

            var result = await _llm.ExecuteAsync(agent, task.Input ?? string.Empty, task.SystemPromptSnapshot ?? agent.SystemPrompt, cancellationToken, task.Id);

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
            await _events.LogAsync(
                task.Id,
                task.AgentId,
                TaskExecutionEventType.TaskCompleted,
                "Task completed successfully.",
                new
                {
                    result.TotalTokens,
                    result.CostUSD,
                    result.ModelUsed,
                    result.ToolCallsUsed,
                    task.DurationMs
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
            await _events.LogAsync(
                task.Id,
                task.AgentId,
                TaskExecutionEventType.TaskCancelled,
                "Task cancelled during execution.");
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
            await _events.LogAsync(
                task.Id,
                task.AgentId,
                TaskExecutionEventType.TaskFailed,
                "Task failed during execution.",
                new { ex.Message, task.DurationMs });
        }

        await db.SaveChangesAsync();
        if (task.Status == AgentTaskStatus.Completed)
            await TryCaptureCompletedTaskMemoriesAsync(agent, task);
        _realtime.NotifyTaskCompleted(new TaskCompletedNotification(
            task.Id, task.AgentId, task.AgentName, task.Name, task.Status));
        await _realtime.TaskUpdatedAsync(task.Id, task.AgentId);
        await _realtime.AgentUpdatedAsync(agent.Id);
        await _realtime.DashboardRefreshAsync();
    }

    private async Task TryCaptureCompletedTaskMemoriesAsync(Agent agent, AgentTask task)
    {
        try
        {
            await _memoryCapture.CaptureCompletedTaskMemoriesAsync(agent, task);
        }
        catch (Exception ex)
        {
            await _events.LogAsync(
                task.Id,
                task.AgentId,
                TaskExecutionEventType.TaskWarning,
                "Task completed, but memory capture failed.",
                new { ex.Message });
        }
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

        if (task.Status is AgentTaskStatus.Failed or AgentTaskStatus.Cancelled)
            _realtime.NotifyTaskCompleted(new TaskCompletedNotification(
                task.Id, task.AgentId, task.AgentName, task.Name, task.Status));
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
            LocalAutomationEnabled = persistedAgent.LocalAutomationEnabled,
            AllowPowerShellScripts = persistedAgent.AllowPowerShellScripts,
            AllowDestructiveActions = persistedAgent.AllowDestructiveActions,
            LocalAutomationApprovalMode = persistedAgent.LocalAutomationApprovalMode,
            AllowedLocalRootsJson = persistedAgent.AllowedLocalRootsJson,
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

    private static async Task<LocalAutomationPlanningContext> BuildLocalAutomationPlanningContextAsync(AppDbContext db, AgentTask task)
    {
        var originalInput = task.Input ?? string.Empty;

        var recentTouchedPaths = await db.Tasks
            .Where(t => t.AgentId == task.AgentId &&
                        t.Id != task.Id &&
                        t.Status == AgentTaskStatus.Completed &&
                        !string.IsNullOrWhiteSpace(t.TouchedPathsJson))
            .OrderByDescending(t => t.CompletedAt ?? t.CreatedAt)
            .Take(5)
            .Select(t => t.TouchedPathsJson!)
            .ToListAsync();

        var flattenedPaths = recentTouchedPaths
            .SelectMany(ParseTouchedPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (flattenedPaths.Count == 0)
        {
            return new LocalAutomationPlanningContext(originalInput, Array.Empty<string>());
        }

        var sb = new StringBuilder();
        sb.AppendLine("User request:");
        sb.AppendLine(originalInput);
        sb.AppendLine();
        sb.AppendLine("Recent local path context:");
        foreach (var path in flattenedPaths)
        {
            sb.AppendLine($"- {path}");
        }

        sb.AppendLine();
        sb.AppendLine("If the request refers to one of these folders or files by name, reuse the exact absolute path from this context.");
        sb.AppendLine("Do not change the drive letter unless the user explicitly requests a different path.");
        return new LocalAutomationPlanningContext(sb.ToString().Trim(), flattenedPaths);
    }

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

    private static IEnumerable<string> ParseTouchedPaths(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            yield break;
        }

        List<string>? paths = null;
        try
        {
            paths = JsonSerializer.Deserialize<List<string>>(json);
        }
        catch
        {
            yield break;
        }

        if (paths is null)
        {
            yield break;
        }

        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            yield return path;
        }
    }

    private static LocalAutomationPlan? TryBuildRecentFolderShortcut(
        string input,
        IReadOnlyList<string> recentTouchedPaths,
        string? allowedRootsJson)
    {
        if (string.IsNullOrWhiteSpace(input) || recentTouchedPaths.Count == 0)
            recentTouchedPaths = Array.Empty<string>();

        if (!input.Contains("create", StringComparison.OrdinalIgnoreCase) ||
            !input.Contains("file", StringComparison.OrdinalIgnoreCase) ||
            !input.Contains("folder", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var fileMatch = Regex.Match(input, @"named\s+['""]?(?<file>[^'""]+\.[A-Za-z0-9]+)['""]?", RegexOptions.IgnoreCase);
        if (!fileMatch.Success)
        {
            return null;
        }

        var folderMatches = recentTouchedPaths
            .Select(path => LocalAutomationPathResolver.NormalizePath(path))
            .Where(path =>
            {
                var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return !string.IsNullOrWhiteSpace(name) &&
                       input.Contains(name, StringComparison.OrdinalIgnoreCase);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (folderMatches.Count == 0)
        {
            folderMatches = FindMatchingFoldersInAllowedRoots(input, allowedRootsJson);
        }

        if (folderMatches.Count != 1)
        {
            return null;
        }

        return new LocalAutomationPlan
        {
            Summary = $"Create file in {folderMatches[0]}",
            Actions =
            [
                new LocalAutomationAction
                {
                    Type = LocalAutomationActionType.WriteTextFile,
                    Path = Path.Combine(folderMatches[0], fileMatch.Groups["file"].Value),
                    Content = string.Empty
                }
            ]
        };
    }

    private static List<string> FindMatchingFoldersInAllowedRoots(string input, string? allowedRootsJson)
    {
        if (string.IsNullOrWhiteSpace(allowedRootsJson))
        {
            return new List<string>();
        }

        List<string>? allowedRoots = null;
        try
        {
            allowedRoots = JsonSerializer.Deserialize<List<string>>(allowedRootsJson);
        }
        catch
        {
            return new List<string>();
        }

        if (allowedRoots is null || allowedRoots.Count == 0)
        {
            return new List<string>();
        }

        var requestedFolderNames = allowedRoots
            .SelectMany(root => Directory.Exists(root)
                ? Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                : Array.Empty<string>())
            .Select(path => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            .Where(name => !string.IsNullOrWhiteSpace(name) && input.Contains(name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requestedFolderNames.Count != 1)
        {
            return new List<string>();
        }

        var targetName = requestedFolderNames[0];
        return allowedRoots
            .SelectMany(root => Directory.Exists(root)
                ? Directory.GetDirectories(root, targetName, SearchOption.AllDirectories)
                : Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed class TaskExecutionProfile
    {
        public Agent RuntimeAgent { get; set; } = new();
    }

    private sealed record LocalAutomationPlanningContext(string Input, IReadOnlyList<string> RecentTouchedPaths);

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
