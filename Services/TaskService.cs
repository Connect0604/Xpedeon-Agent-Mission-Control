using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class TaskService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly LLMExecutionService _llm;

    public TaskService(IDbContextFactory<AppDbContext> factory, LLMExecutionService llm)
    {
        _factory = factory;
        _llm = llm;
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

        var agent = await db.Agents.Include(a => a.LLMProvider).FirstOrDefaultAsync(a => a.Id == agentId)
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
            return task;
        }

        db.Tasks.Add(task);
        agent.Status = AgentStatus.Active;
        agent.CurrentTask = taskName;
        await db.SaveChangesAsync();

        // Execute async (fire and update)
        _ = Task.Run(async () => await ExecuteTaskAsync(task.Id, agent));

        return task;
    }

    public async Task ApproveTaskAsync(string taskId)
    {
        await using var db = _factory.CreateDbContext();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId);
        if (task == null || task.Status != AgentTaskStatus.PendingApproval) return;

        var agent = await db.Agents.Include(a => a.LLMProvider).FirstOrDefaultAsync(a => a.Id == task.AgentId);
        if (agent == null) return;

        task.Status = AgentTaskStatus.Running;
        task.StartedAt = DateTime.UtcNow;
        agent.Status = AgentStatus.Active;
        agent.CurrentTask = task.Name;
        await db.SaveChangesAsync();

        _ = Task.Run(async () => await ExecuteTaskAsync(task.Id, agent));
    }

    public async Task CancelTaskAsync(string taskId)
    {
        await using var db = _factory.CreateDbContext();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId);
        if (task == null) return;
        task.Status = AgentTaskStatus.Cancelled;
        task.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
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
    }

    private async Task ExecuteTaskAsync(string taskId, Agent agent)
    {
        await using var db = _factory.CreateDbContext();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId);
        if (task == null) return;

        try
        {
            task.Progress = 10;
            await db.SaveChangesAsync();

            var result = await _llm.ExecuteAsync(agent, task.Input ?? string.Empty, task.SystemPromptSnapshot ?? agent.SystemPrompt);

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
                Level = LogLevel.Success, Timestamp = DateTime.UtcNow
            });
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
                Level = LogLevel.Error, Timestamp = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
    }
}
