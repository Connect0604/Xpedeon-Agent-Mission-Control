using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

/// <summary>
/// Handles dynamic agent spawning — splits tasks at runtime based on
/// rule-based patterns or LLM decomposition, runs child agents, and
/// aggregates results back to the parent task.
/// </summary>
public class DynamicSpawnService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly LLMExecutionService _llm;
    private readonly IServiceProvider _services;
    private readonly ILogger<DynamicSpawnService> _logger;

    public DynamicSpawnService(
        IDbContextFactory<AppDbContext> factory,
        LLMExecutionService llm,
        IServiceProvider services,
        ILogger<DynamicSpawnService> logger)
    {
        _factory = factory;
        _llm = llm;
        _services = services;
        _logger = logger;
    }

    // ── Entry point called from TaskService ──────────────────────────

    public async Task ExecuteWithSpawningAsync(string parentTaskId, Agent parentAgent)
    {
        await using var db = _factory.CreateDbContext();
        var parentTask = await db.Tasks.FirstOrDefaultAsync(t => t.Id == parentTaskId);
        if (parentTask == null) return;

        try
        {
            // Guard: depth check
            if (parentTask.SpawnDepth >= parentAgent.MaxDepth)
            {
                await FallbackToDirectAsync(parentTaskId, parentAgent);
                return;
            }

            // Guard: spawn count budget
            if (parentAgent.MaxSpawns <= 0)
            {
                await FallbackToDirectAsync(parentTaskId, parentAgent);
                return;
            }

            parentTask.Progress = 5;
            await db.SaveChangesAsync();

            // Decompose into sub-tasks
            var subTasks = parentAgent.SpawnMode == AgentSpawnMode.LLMDecided
                ? await DecomposeLLMAsync(parentAgent, parentTask.Input ?? string.Empty)
                : DecomposeRuleBased(parentAgent, parentTask.Input ?? string.Empty);

            if (subTasks.Count == 0)
            {
                await FallbackToDirectAsync(parentTaskId, parentAgent);
                return;
            }

            // Cap at MaxSpawns
            if (subTasks.Count > parentAgent.MaxSpawns)
                subTasks = subTasks.Take(parentAgent.MaxSpawns).ToList();

            parentTask.SpawnChildCount = subTasks.Count;
            parentTask.Progress = 10;
            await db.SaveChangesAsync();

            _logger.LogInformation("Spawning {Count} child agents for task {TaskId}", subTasks.Count, parentTaskId);

            // Spawn + run child agents
            var childTaskIds = new List<string>();
            foreach (var (childName, childPrompt, childInput) in subTasks)
            {
                var childAgent = await SpawnChildAgentAsync(parentAgent, childName, childPrompt, parentTask.SpawnDepth + 1);
                var childTaskId = await CreateChildTaskAsync(childAgent, parentTaskId, childName, childInput, parentTask.Priority, parentTask.SpawnDepth + 1);
                childTaskIds.Add(childTaskId);
            }

            // Wait for all children to finish (poll, with timeout)
            var timeout = TimeSpan.FromSeconds(parentAgent.TimeoutSeconds * subTasks.Count);
            await WaitForChildrenAsync(childTaskIds, timeout);

            parentTask.Progress = 90;
            await db.SaveChangesAsync();

            // Aggregate results
            var childOutputs = await CollectChildOutputsAsync(childTaskIds);
            var aggregated = await AggregateAsync(parentAgent, childOutputs);

            // Write result back to parent task
            parentTask.Output = aggregated;
            parentTask.Status = AgentTaskStatus.Completed;
            parentTask.CompletedAt = DateTime.UtcNow;
            parentTask.Progress = 100;

            // Sum token costs from children
            var childTasks = await db.Tasks.Where(t => childTaskIds.Contains(t.Id)).ToListAsync();
            parentTask.PromptTokens     = childTasks.Sum(t => t.PromptTokens);
            parentTask.CompletionTokens = childTasks.Sum(t => t.CompletionTokens);
            parentTask.TotalTokens      = childTasks.Sum(t => t.TotalTokens);
            parentTask.CostUSD          = childTasks.Sum(t => t.CostUSD);

            // Update parent agent
            var agentRecord = await db.Agents.FindAsync(parentAgent.Id);
            if (agentRecord != null)
            {
                agentRecord.TotalTokensUsed += parentTask.TotalTokens;
                agentRecord.TotalCostUSD    += parentTask.CostUSD;
                agentRecord.TasksCompleted++;
                agentRecord.Status      = AgentStatus.Idle;
                agentRecord.CurrentTask = "Idle";
            }

            db.Logs.Add(new LogEntry
            {
                AgentId   = parentAgent.Id,
                AgentName = parentAgent.Name,
                TaskId    = parentTaskId,
                Message   = $"Spawned {subTasks.Count} child agents — aggregated result ready",
                Level     = AgentLogLevel.Success,
                Timestamp = DateTime.UtcNow
            });

            await db.SaveChangesAsync();

            // Cleanup ephemeral children
            if (parentAgent.SpawnLifecycle == AgentSpawnLifecycle.Ephemeral)
                await CleanupEphemeralAsync(childTaskIds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dynamic spawn failed for task {TaskId}", parentTaskId);

            await using var db2 = _factory.CreateDbContext();
            var t = await db2.Tasks.FindAsync(parentTaskId);
            if (t != null)
            {
                t.Status       = AgentTaskStatus.Failed;
                t.ErrorMessage = $"Spawn failed: {ex.Message}";
                t.CompletedAt  = DateTime.UtcNow;
            }
            var a = await db2.Agents.FindAsync(parentAgent.Id);
            if (a != null) { a.TasksFailed++; a.Status = AgentStatus.Error; }
            await db2.SaveChangesAsync();
        }
    }

    // ── Decomposition ────────────────────────────────────────────────

    private static List<(string Name, string Prompt, string Input)> DecomposeRuleBased(Agent agent, string input)
    {
        var items = agent.SpawnTriggerType switch
        {
            AgentSpawnTriggerType.SplitByNewline => input.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            AgentSpawnTriggerType.SplitByComma   => input.Split(',',  StringSplitOptions.RemoveEmptyEntries),
            AgentSpawnTriggerType.JsonArray       => TryParseJsonArray(input),
            _ => input.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        };

        return items.Select((item, i) =>
        {
            var childInput = item.Trim();
            var childPrompt = string.IsNullOrWhiteSpace(agent.ChildPromptTemplate)
                ? agent.SystemPrompt
                : agent.ChildPromptTemplate.Replace("{item}", childInput).Replace("{n}", (i + 1).ToString());
            return ($"Item {i + 1}", childPrompt, childInput);
        }).ToList();
    }

    private async Task<List<(string Name, string Prompt, string Input)>> DecomposeLLMAsync(Agent agent, string input)
    {
        var orchestratorPrompt = @"You are a task decomposer. Analyse the input and break it into the smallest independent sub-tasks that can each be handled by a separate AI agent.

Output ONLY a valid JSON array. Each element must have exactly three fields:
  ""name""   — short label for this sub-task
  ""prompt"" — the system prompt for the agent handling this sub-task
  ""input""  — the specific input for this sub-task

Example:
[
  { ""name"": ""Extract line items"", ""prompt"": ""Extract all invoice line items as JSON"", ""input"": ""Invoice #1001..."" },
  { ""name"": ""Validate totals"",    ""prompt"": ""Validate that line item totals match invoice total"", ""input"": ""Line items: ..."" }
]

Do not include any text outside the JSON array.";

        try
        {
            var stub = new Agent { LLMProvider = agent.LLMProvider, SystemPrompt = orchestratorPrompt };
            var result = await _llm.ExecuteAsync(stub, input, orchestratorPrompt);
            return ParseDecompositionJson(result.Output);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM decomposition failed, falling back to newline split");
            return DecomposeRuleBased(agent with { SpawnTriggerType = AgentSpawnTriggerType.SplitByNewline }, input);
        }
    }

    private static List<(string Name, string Prompt, string Input)> ParseDecompositionJson(string json)
    {
        var result = new List<(string, string, string)>();
        try
        {
            // Extract JSON array from response (LLM may add preamble)
            var start = json.IndexOf('[');
            var end   = json.LastIndexOf(']');
            if (start < 0 || end <= start) return result;

            var jsonSlice = json[start..(end + 1)];
            var items = JsonSerializer.Deserialize<List<JsonElement>>(jsonSlice);
            if (items == null) return result;

            foreach (var el in items)
            {
                var name   = el.TryGetProperty("name",   out var n) ? n.GetString() ?? "" : "";
                var prompt = el.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "";
                var input  = el.TryGetProperty("input",  out var i) ? i.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(input))
                    result.Add((name, prompt, input));
            }
        }
        catch { /* return empty, caller falls back */ }
        return result;
    }

    private static string[] TryParseJsonArray(string input)
    {
        try
        {
            var items = JsonSerializer.Deserialize<List<string>>(input);
            return items?.ToArray() ?? input.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch
        {
            return input.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
    }

    // ── Agent + Task creation ────────────────────────────────────────

    private async Task<Agent> SpawnChildAgentAsync(Agent parent, string name, string prompt, int depth)
    {
        await using var db = _factory.CreateDbContext();

        var child = new Agent
        {
            Id             = $"sp-{Guid.NewGuid():N}"[..12],
            Name           = $"{parent.Name} › {name}",
            Type           = parent.Type,
            Description    = $"Spawned child of {parent.Name}",
            SystemPrompt   = string.IsNullOrWhiteSpace(prompt) ? parent.SystemPrompt : prompt,
            LLMProviderId  = parent.LLMProviderId,
            Status         = AgentStatus.Active,
            Version        = parent.Version,
            IsEphemeral    = parent.SpawnLifecycle == AgentSpawnLifecycle.Ephemeral,
            ParentAgentId  = parent.Id,
            SpawnDepth     = depth,
            SpawnEnabled   = false,   // children don't re-spawn unless explicitly configured
            CreatedAt      = DateTime.UtcNow,
            StartedAt      = DateTime.UtcNow,
            LastSeen       = DateTime.UtcNow,
            TimeoutSeconds = parent.TimeoutSeconds,
            MaxRetries     = parent.MaxRetries
        };

        db.Agents.Add(child);
        await db.SaveChangesAsync();
        return child;
    }

    private async Task<string> CreateChildTaskAsync(Agent child, string parentTaskId, string name, string input, TaskPriority priority, int depth)
    {
        await using var db = _factory.CreateDbContext();

        var task = new AgentTask
        {
            Id                = $"t-{Guid.NewGuid():N}"[..12],
            AgentId           = child.Id,
            AgentName         = child.Name,
            Name              = name,
            Description       = name,
            TaskType          = child.Type.ToString(),
            Status            = AgentTaskStatus.Running,
            Priority          = priority,
            TriggerSource     = TriggerSource.Swarm,
            Input             = input,
            SystemPromptSnapshot = child.SystemPrompt,
            SpawnParentTaskId = parentTaskId,
            SpawnDepth        = depth,
            CreatedAt         = DateTime.UtcNow,
            StartedAt         = DateTime.UtcNow,
            Progress          = 0
        };

        db.Tasks.Add(task);
        await db.SaveChangesAsync();

        // Load child agent with LLMProvider for execution
        var childWithProvider = await db.Agents.Include(a => a.LLMProvider).FirstOrDefaultAsync(a => a.Id == child.Id);
        if (childWithProvider != null)
            _ = Task.Run(async () => await RunChildTaskAsync(task.Id, childWithProvider));

        return task.Id;
    }

    private async Task RunChildTaskAsync(string taskId, Agent agent)
    {
        await using var db = _factory.CreateDbContext();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId);
        if (task == null) return;

        try
        {
            task.Progress = 20;
            await db.SaveChangesAsync();

            var result = await _llm.ExecuteAsync(agent, task.Input ?? string.Empty, task.SystemPromptSnapshot ?? agent.SystemPrompt);

            task.Output             = result.Output;
            task.PromptTokens       = result.PromptTokens;
            task.CompletionTokens   = result.CompletionTokens;
            task.TotalTokens        = result.TotalTokens;
            task.CostUSD            = result.CostUSD;
            task.ModelUsed          = result.ModelUsed;
            task.ConfidenceScore    = result.ConfidenceScore;
            task.Status             = AgentTaskStatus.Completed;
            task.CompletedAt        = DateTime.UtcNow;
            task.Progress           = 100;

            var agentRecord = await db.Agents.FindAsync(agent.Id);
            if (agentRecord != null)
            {
                agentRecord.TotalTokensUsed += result.TotalTokens;
                agentRecord.TotalCostUSD    += result.CostUSD;
                agentRecord.TasksCompleted++;
                agentRecord.Status = agent.IsEphemeral ? AgentStatus.Offline : AgentStatus.Idle;
            }
        }
        catch (Exception ex)
        {
            task.Status       = AgentTaskStatus.Failed;
            task.ErrorMessage = ex.Message;
            task.CompletedAt  = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    // ── Wait + Aggregate ─────────────────────────────────────────────

    private async Task WaitForChildrenAsync(List<string> childTaskIds, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(2000);
            await using var db = _factory.CreateDbContext();
            var pending = await db.Tasks
                .Where(t => childTaskIds.Contains(t.Id) &&
                            t.Status != AgentTaskStatus.Completed &&
                            t.Status != AgentTaskStatus.Failed &&
                            t.Status != AgentTaskStatus.Cancelled)
                .CountAsync();
            if (pending == 0) return;
        }
        _logger.LogWarning("Timed out waiting for {Count} child tasks", childTaskIds.Count);
    }

    private async Task<List<(string Name, string Output)>> CollectChildOutputsAsync(List<string> childTaskIds)
    {
        await using var db = _factory.CreateDbContext();
        var tasks = await db.Tasks.Where(t => childTaskIds.Contains(t.Id)).ToListAsync();
        return tasks
            .OrderBy(t => t.CreatedAt)
            .Select(t => (t.Name, t.Output ?? $"[{t.Status}]"))
            .ToList();
    }

    private async Task<string> AggregateAsync(Agent agent, List<(string Name, string Output)> outputs)
    {
        if (outputs.Count == 0) return "No results from child agents.";

        return agent.SpawnAggregation switch
        {
            AgentSpawnAggregation.FirstWins =>
                outputs.FirstOrDefault(o => !o.Output.StartsWith("[")).Output ?? outputs[0].Output,

            AgentSpawnAggregation.Voting =>
                outputs.GroupBy(o => o.Output)
                       .OrderByDescending(g => g.Count())
                       .First().Key,

            AgentSpawnAggregation.LLMSynthesize =>
                await SynthesizeLLMAsync(agent, outputs),

            _ => // CollectAll
                string.Join("\n\n", outputs.Select((o, i) => $"### Result {i + 1}: {o.Name}\n{o.Output}"))
        };
    }

    private async Task<string> SynthesizeLLMAsync(Agent agent, List<(string Name, string Output)> outputs)
    {
        var combined = string.Join("\n\n", outputs.Select((o, i) => $"[Agent {i + 1} — {o.Name}]\n{o.Output}"));
        var synthesisPrompt = "You are a results synthesizer. Combine the following agent outputs into a single coherent, comprehensive response. Remove duplicate information.";
        try
        {
            var stub = new Agent { LLMProvider = agent.LLMProvider };
            var result = await _llm.ExecuteAsync(stub, combined, synthesisPrompt);
            return result.Output;
        }
        catch
        {
            return string.Join("\n\n", outputs.Select((o, i) => $"### Result {i + 1}: {o.Name}\n{o.Output}"));
        }
    }

    // ── Cleanup + Fallback ───────────────────────────────────────────

    private async Task CleanupEphemeralAsync(List<string> childTaskIds)
    {
        try
        {
            await using var db = _factory.CreateDbContext();
            var childAgentIds = await db.Tasks
                .Where(t => childTaskIds.Contains(t.Id))
                .Select(t => t.AgentId)
                .ToListAsync();

            var ephemeralAgents = await db.Agents
                .Where(a => childAgentIds.Contains(a.Id) && a.IsEphemeral)
                .ToListAsync();

            db.Agents.RemoveRange(ephemeralAgents);
            await db.SaveChangesAsync();

            _logger.LogInformation("Cleaned up {Count} ephemeral agents", ephemeralAgents.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ephemeral cleanup failed (non-critical)");
        }
    }

    private async Task FallbackToDirectAsync(string taskId, Agent agent)
    {
        await using var db = _factory.CreateDbContext();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId);
        if (task == null) return;

        try
        {
            var result = await _llm.ExecuteAsync(agent, task.Input ?? string.Empty, task.SystemPromptSnapshot ?? agent.SystemPrompt);
            task.Output             = result.Output;
            task.PromptTokens       = result.PromptTokens;
            task.CompletionTokens   = result.CompletionTokens;
            task.TotalTokens        = result.TotalTokens;
            task.CostUSD            = result.CostUSD;
            task.ModelUsed          = result.ModelUsed;
            task.Status             = AgentTaskStatus.Completed;
            task.CompletedAt        = DateTime.UtcNow;
            task.Progress           = 100;
        }
        catch (Exception ex)
        {
            task.Status       = AgentTaskStatus.Failed;
            task.ErrorMessage = ex.Message;
            task.CompletedAt  = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    // ── Query helpers ────────────────────────────────────────────────

    public async Task<List<AgentTask>> GetChildTasksAsync(string parentTaskId)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Tasks
            .Where(t => t.SpawnParentTaskId == parentTaskId)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<Agent>> GetChildAgentsAsync(string parentAgentId)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Agents
            .Where(a => a.ParentAgentId == parentAgentId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();
    }
}
