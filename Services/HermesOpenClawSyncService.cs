using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Configuration;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class HermesOpenClawSyncService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HermesOpenClawConfig _config;
    private readonly ILogger<HermesOpenClawSyncService> _logger;

    public HermesOpenClawSyncService(
        IServiceScopeFactory scopeFactory,
        HermesOpenClawConfig config,
        ILogger<HermesOpenClawSyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Enabled)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncPendingRunsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hermes/OpenClaw sync loop failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, _config.PollIntervalSeconds)), stoppingToken);
        }
    }

    private async Task SyncPendingRunsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var hermes = scope.ServiceProvider.GetRequiredService<HermesOpenClawExecutionService>();
        var realtime = scope.ServiceProvider.GetRequiredService<RealtimeService>();

        if (!hermes.IsConfigured)
            return;

        await using var db = factory.CreateDbContext();
        var candidates = await db.Tasks
            .Where(t => t.ExecutionBackendSnapshot == ExecutionBackend.HermesOpenClaw
                     && !string.IsNullOrWhiteSpace(t.ExternalRunId)
                     && (t.ExternalStatus == ExternalRunStatus.Submitted || t.ExternalStatus == ExternalRunStatus.Running))
            .OrderBy(t => t.CreatedAt)
            .Take(Math.Max(1, _config.SyncBatchSize))
            .ToListAsync(cancellationToken);

        if (!candidates.Any())
            return;

        foreach (var task in candidates)
        {
            try
            {
                var sync = await hermes.GetRunStatusAsync(task.ExternalRunId!, cancellationToken);
                task.ExternalStatus = sync.Status;
                task.ExternalLastSyncedAt = DateTime.UtcNow;
                task.ExternalTrace = sync.Trace ?? task.ExternalTrace;
                task.ExternalResultJson = sync.RawResponse;
                task.ExternalError = sync.ErrorMessage;

                if (HermesOpenClawExecutionService.IsTerminal(sync.Status))
                {
                    task.CompletedAt = DateTime.UtcNow;
                    task.DurationMs = task.StartedAt.HasValue
                        ? (long)Math.Max(0, (task.CompletedAt.Value - task.StartedAt.Value).TotalMilliseconds)
                        : task.DurationMs;
                    task.PromptTokens = sync.PromptTokens;
                    task.CompletionTokens = sync.CompletionTokens;
                    task.TotalTokens = sync.TotalTokens;
                    task.CostUSD = sync.CostUsd;
                    task.ToolCallsUsed = sync.ToolCallsUsed;
                    task.ConfidenceScore = sync.ConfidenceScore;
                    task.ModelUsed = sync.ModelUsed ?? task.ModelUsed;
                    task.ExecutionTrace = string.Join(Environment.NewLine, new[]
                    {
                        task.ExecutionTrace,
                        sync.Trace
                    }.Where(x => !string.IsNullOrWhiteSpace(x)));

                    var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == task.AgentId, cancellationToken);

                    if (sync.Status == ExternalRunStatus.Completed)
                    {
                        task.Status = AgentTaskStatus.Completed;
                        task.Output = sync.Output;
                        task.Progress = 100;

                        if (agent != null)
                        {
                            agent.TasksCompleted++;
                            agent.TotalTokensUsed += sync.TotalTokens;
                            agent.TotalCostUSD += sync.CostUsd;
                            agent.Status = AgentStatus.Idle;
                            agent.CurrentTask = "Idle";
                        }

                        db.Logs.Add(new LogEntry
                        {
                            AgentId = task.AgentId,
                            AgentName = task.AgentName,
                            TaskId = task.Id,
                            Message = $"Delegated task '{task.Name}' completed via Hermes/OpenClaw.",
                            Level = AgentLogLevel.Success,
                            Timestamp = DateTime.UtcNow
                        });
                    }
                    else
                    {
                        task.Status = sync.Status == ExternalRunStatus.Cancelled
                            ? AgentTaskStatus.Cancelled
                            : AgentTaskStatus.Failed;
                        task.ErrorMessage = sync.ErrorMessage ?? $"Delegated run ended with status {sync.Status}.";
                        task.Progress = 0;

                        if (agent != null)
                        {
                            if (task.Status == AgentTaskStatus.Failed)
                                agent.TasksFailed++;
                            agent.Status = task.Status == AgentTaskStatus.Cancelled ? AgentStatus.Idle : AgentStatus.Error;
                            agent.CurrentTask = "Idle";
                        }

                        db.Logs.Add(new LogEntry
                        {
                            AgentId = task.AgentId,
                            AgentName = task.AgentName,
                            TaskId = task.Id,
                            Message = $"Delegated task '{task.Name}' ended with status {sync.Status}: {task.ErrorMessage}",
                            Level = task.Status == AgentTaskStatus.Cancelled ? AgentLogLevel.Warning : AgentLogLevel.Error,
                            Timestamp = DateTime.UtcNow
                        });
                    }
                }
                else
                {
                    task.Status = AgentTaskStatus.Running;
                    task.Progress = Math.Max(task.Progress, 35);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed syncing delegated task {TaskId}", task.Id);
                task.ExternalLastSyncedAt = DateTime.UtcNow;
                task.ExternalError = ex.Message;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var task in candidates)
        {
            await realtime.TaskUpdatedAsync(task.Id, task.AgentId);
            await realtime.AgentUpdatedAsync(task.AgentId);
        }

        await realtime.DashboardRefreshAsync();
    }
}
