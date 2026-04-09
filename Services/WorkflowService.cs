using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class WorkflowService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly TaskService _taskService;
    private readonly SwarmService _swarmService;
    private readonly RealtimeService _realtime;
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> RunCancellation = new();

    public WorkflowService(
        IDbContextFactory<AppDbContext> factory,
        TaskService taskService,
        SwarmService swarmService,
        RealtimeService realtime)
    {
        _factory = factory;
        _taskService = taskService;
        _swarmService = swarmService;
        _realtime = realtime;
    }

    public async Task<List<AgentWorkflow>> GetAllAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.Workflows
            .Include(w => w.Steps.OrderBy(s => s.StepOrder))
            .Include(w => w.Runs.OrderByDescending(r => r.CreatedAt).Take(5))
            .OrderByDescending(w => w.CreatedAt)
            .ToListAsync();
    }

    public async Task<AgentWorkflow?> GetByIdAsync(string workflowId)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Workflows
            .Include(w => w.Steps.OrderBy(s => s.StepOrder)).ThenInclude(s => s.Agent)
            .Include(w => w.Steps.OrderBy(s => s.StepOrder)).ThenInclude(s => s.Swarm)
            .Include(w => w.Runs.OrderByDescending(r => r.CreatedAt).Take(20))
                .ThenInclude(r => r.StepRuns.OrderBy(sr => sr.StepOrder))
            .FirstOrDefaultAsync(w => w.Id == workflowId);
    }

    public async Task<WorkflowRun?> GetRunAsync(string runId)
    {
        await using var db = _factory.CreateDbContext();
        return await db.WorkflowRuns
            .Include(r => r.Workflow)
            .Include(r => r.StepRuns.OrderBy(sr => sr.StepOrder))
            .FirstOrDefaultAsync(r => r.Id == runId);
    }

    public async Task<List<WorkflowStepRun>> GetStepRunsAsync(string workflowRunId)
    {
        await using var db = _factory.CreateDbContext();
        return await db.WorkflowStepRuns
            .Where(sr => sr.WorkflowRunId == workflowRunId)
            .OrderBy(sr => sr.StepOrder)
            .ToListAsync();
    }

    public async Task<List<AgentTask>> GetTasksForStepRunAsync(string workflowStepRunId)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Tasks
            .Where(t => t.WorkflowStepRunId == workflowStepRunId)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<AgentWorkflow> CreateAsync(AgentWorkflow workflow)
    {
        await using var db = _factory.CreateDbContext();

        workflow.Id = string.IsNullOrWhiteSpace(workflow.Id) ? $"wf-{Guid.NewGuid():N}"[..12] : workflow.Id;
        workflow.CreatedAt = DateTime.UtcNow;

        var orderedSteps = workflow.Steps
            .OrderBy(s => s.StepOrder)
            .Select((step, index) =>
            {
                step.Id = string.IsNullOrWhiteSpace(step.Id) ? $"wfs-{Guid.NewGuid():N}"[..12] : step.Id;
                step.WorkflowId = workflow.Id;
                step.StepOrder = index + 1;
                return step;
            })
            .ToList();

        workflow.Steps = orderedSteps;
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        await _realtime.DashboardRefreshAsync();
        return workflow;
    }

    public async Task<AgentWorkflow> UpdateAsync(AgentWorkflow workflow)
    {
        await using var db = _factory.CreateDbContext();
        var existing = await db.Workflows
            .Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.Id == workflow.Id)
                ?? throw new InvalidOperationException("Workflow not found.");

        existing.Name = workflow.Name;
        existing.Description = workflow.Description;
        existing.IsActive = workflow.IsActive;
        existing.Version = workflow.Version;

        if (existing.Steps.Any())
            db.WorkflowSteps.RemoveRange(existing.Steps);

        existing.Steps = workflow.Steps
            .OrderBy(s => s.StepOrder)
            .Select((step, index) => new AgentWorkflowStep
            {
                Id = string.IsNullOrWhiteSpace(step.Id) ? $"wfs-{Guid.NewGuid():N}"[..12] : step.Id,
                WorkflowId = existing.Id,
                StepOrder = index + 1,
                Name = step.Name,
                StepType = step.StepType,
                AgentId = step.AgentId,
                SwarmId = step.SwarmId,
                StaticInput = step.StaticInput,
                InputSource = step.InputSource,
                PromptOverride = step.PromptOverride,
                RequireApproval = step.RequireApproval,
                TimeoutSeconds = step.TimeoutSeconds,
                ContinueOnFailure = step.ContinueOnFailure
            })
            .ToList();

        await db.SaveChangesAsync();
        await _realtime.DashboardRefreshAsync();
        return existing;
    }

    public async Task DeleteAsync(string workflowId)
    {
        await using var db = _factory.CreateDbContext();
        var workflow = await db.Workflows
            .Include(w => w.Runs).ThenInclude(r => r.StepRuns)
            .Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.Id == workflowId);
        if (workflow == null)
            return;

        var runIds = workflow.Runs.Select(r => r.Id).ToList();
        if (runIds.Any())
        {
            var linkedTasks = await db.Tasks.Where(t => t.WorkflowRunId != null && runIds.Contains(t.WorkflowRunId)).ToListAsync();
            foreach (var task in linkedTasks)
            {
                task.WorkflowRunId = null;
                task.WorkflowStepId = null;
                task.WorkflowStepRunId = null;
            }
        }

        db.Workflows.Remove(workflow);
        await db.SaveChangesAsync();
        await _realtime.DashboardRefreshAsync();
    }

    public async Task<WorkflowRun> StartRunAsync(string workflowId, string input, TaskPriority priority)
    {
        await using var db = _factory.CreateDbContext();
        var workflow = await db.Workflows
            .Include(w => w.Steps.OrderBy(s => s.StepOrder))
            .FirstOrDefaultAsync(w => w.Id == workflowId)
                ?? throw new InvalidOperationException("Workflow not found.");

        if (!workflow.IsActive)
            throw new InvalidOperationException("Workflow is inactive.");

        var run = new WorkflowRun
        {
            WorkflowId = workflow.Id,
            WorkflowNameSnapshot = workflow.Name,
            Status = WorkflowRunStatus.Queued,
            Input = input,
            CreatedAt = DateTime.UtcNow,
            CurrentStepOrder = workflow.Steps.FirstOrDefault()?.StepOrder ?? 0
        };

        db.WorkflowRuns.Add(run);
        await db.SaveChangesAsync();

        BeginRun(run.Id, priority, run.CurrentStepOrder);
        return run;
    }

    public async Task ApproveStepAsync(string workflowStepRunId, string? comment)
    {
        await using var db = _factory.CreateDbContext();
        var stepRun = await db.WorkflowStepRuns.FirstOrDefaultAsync(sr => sr.Id == workflowStepRunId)
            ?? throw new InvalidOperationException("Workflow step run not found.");
        var workflowRun = await db.WorkflowRuns.FirstOrDefaultAsync(r => r.Id == stepRun.WorkflowRunId)
            ?? throw new InvalidOperationException("Workflow run not found.");
        var step = await db.WorkflowSteps.FirstOrDefaultAsync(s => s.Id == stepRun.WorkflowStepId)
            ?? throw new InvalidOperationException("Workflow step not found.");

        if (stepRun.Status != WorkflowRunStatus.PendingApproval)
            return;

        if (step.StepType == WorkflowStepType.ApprovalGate)
        {
            stepRun.Status = WorkflowRunStatus.Completed;
            stepRun.Output = string.IsNullOrWhiteSpace(comment) ? "Approved" : $"Approved: {comment}";
            stepRun.CompletedAt = DateTime.UtcNow;
            workflowRun.Status = WorkflowRunStatus.Running;
            workflowRun.ErrorMessage = null;
            await db.SaveChangesAsync();
            await _realtime.DashboardRefreshAsync();
            BeginRun(workflowRun.Id, TaskPriority.Medium, step.StepOrder + 1);
            return;
        }

        var linkedTasks = await db.Tasks
            .Where(t => t.WorkflowStepRunId == workflowStepRunId && t.Status == AgentTaskStatus.PendingApproval)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();

        if (linkedTasks.Any())
        {
            foreach (var task in linkedTasks)
                await _taskService.ApproveTaskAsync(task.Id, comment);

            workflowRun.Status = WorkflowRunStatus.Running;
            stepRun.Status = WorkflowRunStatus.Running;
            stepRun.StartedAt ??= DateTime.UtcNow;
            await db.SaveChangesAsync();
            await _realtime.DashboardRefreshAsync();
            BeginRun(workflowRun.Id, TaskPriority.Medium, step.StepOrder, workflowStepRunId, true);
            return;
        }

        workflowRun.Status = WorkflowRunStatus.Running;
        stepRun.Status = WorkflowRunStatus.Queued;
        stepRun.ErrorMessage = null;
        await db.SaveChangesAsync();
        await _realtime.DashboardRefreshAsync();
        BeginRun(workflowRun.Id, TaskPriority.Medium, step.StepOrder, workflowStepRunId, true);
    }

    public async Task CancelRunAsync(string workflowRunId)
    {
        if (RunCancellation.TryRemove(workflowRunId, out var source))
        {
            source.Cancel();
            source.Dispose();
        }

        await using var db = _factory.CreateDbContext();
        var run = await db.WorkflowRuns.FirstOrDefaultAsync(r => r.Id == workflowRunId);
        if (run == null)
            return;

        run.Status = WorkflowRunStatus.Cancelled;
        run.CompletedAt = DateTime.UtcNow;

        var activeStepRuns = await db.WorkflowStepRuns
            .Where(sr => sr.WorkflowRunId == workflowRunId &&
                (sr.Status == WorkflowRunStatus.Running || sr.Status == WorkflowRunStatus.PendingApproval || sr.Status == WorkflowRunStatus.Queued))
            .ToListAsync();

        foreach (var stepRun in activeStepRuns)
        {
            stepRun.Status = WorkflowRunStatus.Cancelled;
            stepRun.CompletedAt = DateTime.UtcNow;
        }

        var activeTasks = await db.Tasks
            .Where(t => t.WorkflowRunId == workflowRunId &&
                (t.Status == AgentTaskStatus.Running || t.Status == AgentTaskStatus.PendingApproval || t.Status == AgentTaskStatus.Queued))
            .ToListAsync();

        await db.SaveChangesAsync();

        foreach (var task in activeTasks)
            await _taskService.CancelTaskAsync(task.Id, "Workflow run cancelled.");

        await _realtime.DashboardRefreshAsync();
    }

    public async Task RetryStepAsync(string workflowStepRunId)
    {
        await using var db = _factory.CreateDbContext();
        var stepRun = await db.WorkflowStepRuns.FirstOrDefaultAsync(sr => sr.Id == workflowStepRunId)
            ?? throw new InvalidOperationException("Workflow step run not found.");
        var workflowRun = await db.WorkflowRuns.FirstOrDefaultAsync(r => r.Id == stepRun.WorkflowRunId)
            ?? throw new InvalidOperationException("Workflow run not found.");

        stepRun.Status = WorkflowRunStatus.Queued;
        stepRun.ErrorMessage = null;
        stepRun.Output = null;
        stepRun.CompletedAt = null;
        stepRun.StartedAt = null;
        workflowRun.Status = WorkflowRunStatus.Running;
        workflowRun.ErrorMessage = null;
        workflowRun.CompletedAt = null;
        await db.SaveChangesAsync();

        BeginRun(workflowRun.Id, TaskPriority.Medium, stepRun.StepOrder, stepRun.Id, true);
    }

    private void BeginRun(
        string workflowRunId,
        TaskPriority priority,
        int startStepOrder,
        string? existingStepRunId = null,
        bool bypassApproval = false)
    {
        var cts = RunCancellation.GetOrAdd(workflowRunId, _ => new CancellationTokenSource());
        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteWorkflowAsync(workflowRunId, priority, startStepOrder, existingStepRunId, bypassApproval, cts.Token);
            }
            finally
            {
                if (RunCancellation.TryGetValue(workflowRunId, out var current) && current.IsCancellationRequested)
                {
                    RunCancellation.TryRemove(workflowRunId, out _);
                    current.Dispose();
                }
            }
        });
    }

    private async Task ExecuteWorkflowAsync(
        string workflowRunId,
        TaskPriority priority,
        int startStepOrder,
        string? existingStepRunId,
        bool bypassApproval,
        CancellationToken cancellationToken)
    {
        await using var db = _factory.CreateDbContext();
        var workflowRun = await db.WorkflowRuns
            .Include(r => r.Workflow).ThenInclude(w => w!.Steps.OrderBy(s => s.StepOrder))
            .Include(r => r.StepRuns.OrderBy(sr => sr.StepOrder))
            .FirstOrDefaultAsync(r => r.Id == workflowRunId, cancellationToken);

        if (workflowRun?.Workflow == null)
            return;

        workflowRun.Status = WorkflowRunStatus.Running;
        workflowRun.StartedAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await _realtime.DashboardRefreshAsync();

        var steps = workflowRun.Workflow.Steps.OrderBy(s => s.StepOrder).ToList();
        var lastOutput = workflowRun.StepRuns
            .Where(sr => sr.Status == WorkflowRunStatus.Completed)
            .OrderByDescending(sr => sr.StepOrder)
            .Select(sr => sr.Output)
            .FirstOrDefault() ?? workflowRun.Input ?? string.Empty;

        foreach (var step in steps.Where(s => s.StepOrder >= startStepOrder))
        {
            cancellationToken.ThrowIfCancellationRequested();

            workflowRun = await db.WorkflowRuns
                .Include(r => r.StepRuns.OrderBy(sr => sr.StepOrder))
                .FirstAsync(r => r.Id == workflowRunId, cancellationToken);

            workflowRun.Status = WorkflowRunStatus.Running;
            workflowRun.CurrentStepOrder = step.StepOrder;

            var stepRun = !string.IsNullOrWhiteSpace(existingStepRunId) && step.StepOrder == startStepOrder
                ? workflowRun.StepRuns.FirstOrDefault(sr => sr.Id == existingStepRunId)
                : null;

            stepRun ??= new WorkflowStepRun
            {
                WorkflowRunId = workflowRun.Id,
                WorkflowStepId = step.Id,
                StepNameSnapshot = step.Name,
                StepOrder = step.StepOrder,
                Status = WorkflowRunStatus.Queued,
                CreatedAt = DateTime.UtcNow
            };

            if (db.Entry(stepRun).State == EntityState.Detached)
                db.WorkflowStepRuns.Add(stepRun);

            stepRun.Input ??= await ResolveStepInputAsync(db, workflowRun, step);

            if ((step.StepType == WorkflowStepType.ApprovalGate || step.RequireApproval) &&
                !bypassApproval &&
                stepRun.Status != WorkflowRunStatus.PendingApproval)
            {
                stepRun.Status = WorkflowRunStatus.PendingApproval;
                workflowRun.Status = WorkflowRunStatus.PendingApproval;
                stepRun.ErrorMessage = "Waiting for workflow approval.";
                await db.SaveChangesAsync(cancellationToken);
                await _realtime.DashboardRefreshAsync();
                return;
            }

            bypassApproval = false;
            existingStepRunId = null;

            stepRun.Status = WorkflowRunStatus.Running;
            stepRun.StartedAt ??= DateTime.UtcNow;
            stepRun.ErrorMessage = null;
            await db.SaveChangesAsync(cancellationToken);
            await _realtime.DashboardRefreshAsync();

            var outcome = step.StepType switch
            {
                WorkflowStepType.AgentTask => await ExecuteAgentStepAsync(step, stepRun, priority, cancellationToken),
                WorkflowStepType.SwarmDispatch => await ExecuteSwarmStepAsync(step, stepRun, priority, cancellationToken),
                WorkflowStepType.ApprovalGate => new StepOutcome(WorkflowRunStatus.Completed, stepRun.Output ?? "Approved", null, stepRun.AgentTaskId),
                _ => new StepOutcome(WorkflowRunStatus.Failed, null, $"Unsupported workflow step type '{step.StepType}'.", null)
            };

            stepRun = await db.WorkflowStepRuns.FirstAsync(sr => sr.Id == stepRun.Id, cancellationToken);
            workflowRun = await db.WorkflowRuns.FirstAsync(r => r.Id == workflowRunId, cancellationToken);

            if (outcome.Status == WorkflowRunStatus.PendingApproval)
            {
                stepRun.Status = WorkflowRunStatus.PendingApproval;
                stepRun.ErrorMessage = "Waiting for downstream task approval.";
                if (!string.IsNullOrWhiteSpace(outcome.AgentTaskId))
                    stepRun.AgentTaskId = outcome.AgentTaskId;
                workflowRun.Status = WorkflowRunStatus.PendingApproval;
                await db.SaveChangesAsync(cancellationToken);
                await _realtime.DashboardRefreshAsync();
                return;
            }

            stepRun.Status = outcome.Status;
            stepRun.Output = outcome.Output;
            stepRun.ErrorMessage = outcome.ErrorMessage;
            if (!string.IsNullOrWhiteSpace(outcome.AgentTaskId))
                stepRun.AgentTaskId = outcome.AgentTaskId;
            stepRun.CompletedAt = DateTime.UtcNow;

            if (outcome.Status == WorkflowRunStatus.Completed)
            {
                lastOutput = outcome.Output ?? lastOutput;
                await db.SaveChangesAsync(cancellationToken);
                await _realtime.DashboardRefreshAsync();
                continue;
            }

            if (outcome.Status == WorkflowRunStatus.Cancelled)
            {
                workflowRun.Status = WorkflowRunStatus.Cancelled;
                workflowRun.CompletedAt = DateTime.UtcNow;
                workflowRun.ErrorMessage = outcome.ErrorMessage ?? "Workflow run cancelled.";
                await db.SaveChangesAsync(cancellationToken);
                await _realtime.DashboardRefreshAsync();
                return;
            }

            if (!step.ContinueOnFailure)
            {
                workflowRun.Status = WorkflowRunStatus.Failed;
                workflowRun.CompletedAt = DateTime.UtcNow;
                workflowRun.ErrorMessage = outcome.ErrorMessage ?? "Workflow step failed.";
                await db.SaveChangesAsync(cancellationToken);
                await _realtime.DashboardRefreshAsync();
                return;
            }
        }

        workflowRun = await db.WorkflowRuns.FirstAsync(r => r.Id == workflowRunId, cancellationToken);
        workflowRun.Status = WorkflowRunStatus.Completed;
        workflowRun.Output = lastOutput;
        workflowRun.CompletedAt = DateTime.UtcNow;
        workflowRun.ErrorMessage = null;
        await db.SaveChangesAsync(cancellationToken);
        await _realtime.DashboardRefreshAsync();
        if (RunCancellation.TryRemove(workflowRunId, out var source))
            source.Dispose();
    }

    private async Task<StepOutcome> ExecuteAgentStepAsync(
        AgentWorkflowStep step,
        WorkflowStepRun stepRun,
        TaskPriority priority,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(step.AgentId))
            return new StepOutcome(WorkflowRunStatus.Failed, null, "Workflow step has no agent assigned.", null);

        if (!string.IsNullOrWhiteSpace(stepRun.AgentTaskId))
        {
            var existingTask = await _taskService.GetByIdAsync(stepRun.AgentTaskId);
            if (existingTask != null && existingTask.Status != AgentTaskStatus.Failed && existingTask.Status != AgentTaskStatus.Cancelled)
            {
                return await WaitForAgentTaskOutcomeAsync(existingTask.Id, step.TimeoutSeconds, cancellationToken);
            }
        }

        var workflowContext = new WorkflowTaskContext
        {
            WorkflowRunId = stepRun.WorkflowRunId,
            WorkflowStepId = step.Id,
            WorkflowStepRunId = stepRun.Id
        };

        var task = await _taskService.CreateAndRunForWorkflowAsync(
            step.AgentId,
            step.Name,
            stepRun.Input ?? string.Empty,
            priority,
            TriggerSource.Chained,
            workflowContext,
            step.PromptOverride);

        return await WaitForAgentTaskOutcomeAsync(task.Id, step.TimeoutSeconds, cancellationToken);
    }

    private async Task<StepOutcome> ExecuteSwarmStepAsync(
        AgentWorkflowStep step,
        WorkflowStepRun stepRun,
        TaskPriority priority,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(step.SwarmId))
            return new StepOutcome(WorkflowRunStatus.Failed, null, "Workflow step has no swarm assigned.", null);

        await using var db = _factory.CreateDbContext();
        var existingTasks = await db.Tasks
            .Where(t => t.WorkflowStepRunId == stepRun.Id)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

        if (existingTasks.Any() && existingTasks.Any(t => t.Status != AgentTaskStatus.Failed && t.Status != AgentTaskStatus.Cancelled))
        {
            var settledTasks = await WaitForWorkflowTasksAsync(existingTasks.Select(t => t.Id).ToList(), step.TimeoutSeconds, cancellationToken);
            if (settledTasks.Any(t => t.Status == AgentTaskStatus.PendingApproval))
                return new StepOutcome(WorkflowRunStatus.PendingApproval, null, null, null);

            var output = settledTasks.LastOrDefault(t => t.Status == AgentTaskStatus.Completed)?.Output ?? string.Empty;
            if (settledTasks.Any(t => t.Status == AgentTaskStatus.Failed) && settledTasks.All(t => t.Status != AgentTaskStatus.Completed))
            {
                var error = settledTasks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.ErrorMessage))?.ErrorMessage
                    ?? "Swarm dispatch failed.";
                return new StepOutcome(WorkflowRunStatus.Failed, output, error, null);
            }

            return new StepOutcome(WorkflowRunStatus.Completed, output, null, null);
        }

        var result = await _swarmService.ExecuteSwarmAsync(
            step.SwarmId,
            step.Name,
            stepRun.Input ?? string.Empty,
            priority,
            new WorkflowTaskContext
            {
                WorkflowRunId = stepRun.WorkflowRunId,
                WorkflowStepId = step.Id,
                WorkflowStepRunId = stepRun.Id
            },
            step.PromptOverride);

        var tasks = await WaitForWorkflowTasksAsync(result.TaskIds, step.TimeoutSeconds, cancellationToken);
        if (tasks.Any(t => t.Status == AgentTaskStatus.PendingApproval))
            return new StepOutcome(WorkflowRunStatus.PendingApproval, null, null, null);

        if (tasks.Any(t => t.Status == AgentTaskStatus.Failed) && result.CompletedTaskCount == 0)
        {
            var error = tasks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.ErrorMessage))?.ErrorMessage
                ?? "Swarm dispatch failed.";
            return new StepOutcome(WorkflowRunStatus.Failed, result.FinalOutput, error, null);
        }

        return new StepOutcome(WorkflowRunStatus.Completed, result.FinalOutput, null, null);
    }

    private async Task<StepOutcome> WaitForAgentTaskOutcomeAsync(string taskId, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(30, timeoutSeconds));

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var task = await _taskService.GetByIdAsync(taskId);
            if (task == null)
                return new StepOutcome(WorkflowRunStatus.Failed, null, "Linked task was not found.", taskId);

            switch (task.Status)
            {
                case AgentTaskStatus.Completed:
                    return new StepOutcome(WorkflowRunStatus.Completed, task.Output, null, taskId);
                case AgentTaskStatus.PendingApproval:
                    return new StepOutcome(WorkflowRunStatus.PendingApproval, null, null, taskId);
                case AgentTaskStatus.Failed:
                    return new StepOutcome(WorkflowRunStatus.Failed, task.Output, task.ErrorMessage, taskId);
                case AgentTaskStatus.Cancelled:
                    return new StepOutcome(WorkflowRunStatus.Cancelled, task.Output, task.ErrorMessage ?? "Task cancelled.", taskId);
            }

            await Task.Delay(1000, cancellationToken);
        }

        return new StepOutcome(WorkflowRunStatus.Failed, null, "Timed out waiting for task completion.", taskId);
    }

    private async Task<List<AgentTask>> WaitForWorkflowTasksAsync(List<string> taskIds, int timeoutSeconds, CancellationToken cancellationToken)
    {
        if (!taskIds.Any())
            return new List<AgentTask>();

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(30, timeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var db = _factory.CreateDbContext();
            var tasks = await db.Tasks.Where(t => taskIds.Contains(t.Id)).ToListAsync(cancellationToken);
            if (tasks.All(t => t.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled or AgentTaskStatus.PendingApproval))
                return tasks;

            await Task.Delay(1000, cancellationToken);
        }

        await using var timeoutDb = _factory.CreateDbContext();
        return await timeoutDb.Tasks.Where(t => taskIds.Contains(t.Id)).ToListAsync(cancellationToken);
    }

    private static async Task<string> ResolveStepInputAsync(AppDbContext db, WorkflowRun run, AgentWorkflowStep step)
    {
        return step.InputSource switch
        {
            WorkflowInputSource.StaticText => step.StaticInput ?? string.Empty,
            WorkflowInputSource.PreviousStepOutput => await db.WorkflowStepRuns
                .Where(sr => sr.WorkflowRunId == run.Id && sr.StepOrder < step.StepOrder && sr.Status == WorkflowRunStatus.Completed)
                .OrderByDescending(sr => sr.StepOrder)
                .Select(sr => sr.Output)
                .FirstOrDefaultAsync() ?? run.Input ?? string.Empty,
            _ => run.Input ?? string.Empty
        };
    }

    private sealed record StepOutcome(WorkflowRunStatus Status, string? Output, string? ErrorMessage, string? AgentTaskId);
}
