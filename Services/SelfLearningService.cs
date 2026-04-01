using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

/// <summary>
/// Drives the feedback → prompt evolution loop.
/// Analyses task feedback ratings, detects low-performing prompts,
/// suggests refined versions, and records the new PromptHistory entry.
/// </summary>
public class SelfLearningService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly LLMExecutionService _llm;
    private readonly LogService _log;

    public SelfLearningService(
        IDbContextFactory<AppDbContext> factory,
        LLMExecutionService llm,
        LogService log)
    {
        _factory = factory;
        _llm = llm;
        _log = log;
    }

    // ── Public API ──────────────────────────────────────────────────

    /// <summary>Returns aggregated learning stats for an agent.</summary>
    public async Task<AgentLearningStats> GetStatsAsync(string agentId)
    {
        await using var db = _factory.CreateDbContext();

        var tasks = await db.AgentTasks
            .Where(t => t.AgentId == agentId && t.Status == AgentTaskStatus.Completed)
            .ToListAsync();

        var rated    = tasks.Where(t => t.FeedbackRating.HasValue).ToList();
        var positive = rated.Count(t => t.FeedbackRating == 1);
        var negative = rated.Count(t => t.FeedbackRating == -1);

        var history = await db.PromptHistories
            .Where(p => p.AgentId == agentId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

        return new AgentLearningStats
        {
            AgentId          = agentId,
            TotalTasks       = tasks.Count,
            RatedTasks       = rated.Count,
            PositiveRatings  = positive,
            NegativeRatings  = negative,
            SuccessRate      = rated.Count > 0 ? (double)positive / rated.Count * 100 : 0,
            PromptVersions   = history.Count,
            CurrentVersion   = history.FirstOrDefault()?.Version ?? 1,
            PromptHistory    = history,
            RecentTasks      = tasks.OrderByDescending(t => t.CreatedAt).Take(10).ToList()
        };
    }

    /// <summary>
    /// Analyses recent negative feedback and uses the LLM to suggest
    /// an improved system prompt. Does NOT auto-apply — returns the
    /// suggestion so the user can review and approve.
    /// </summary>
    public async Task<PromptRefinementSuggestion> SuggestRefinementAsync(string agentId)
    {
        await using var db = _factory.CreateDbContext();

        var agent = await db.Agents
            .Include(a => a.LLMProvider)
            .FirstOrDefaultAsync(a => a.Id == agentId)
            ?? throw new InvalidOperationException("Agent not found.");

        if (string.IsNullOrWhiteSpace(agent.SystemPrompt))
            throw new InvalidOperationException("Agent has no system prompt to refine.");

        // Collect recent negative-feedback samples
        var negativeTasks = await db.AgentTasks
            .Where(t => t.AgentId == agentId
                     && t.FeedbackRating == -1
                     && !string.IsNullOrEmpty(t.Output))
            .OrderByDescending(t => t.CreatedAt)
            .Take(5)
            .ToListAsync();

        var positiveTasks = await db.AgentTasks
            .Where(t => t.AgentId == agentId
                     && t.FeedbackRating == 1
                     && !string.IsNullOrEmpty(t.Output))
            .OrderByDescending(t => t.CreatedAt)
            .Take(3)
            .ToListAsync();

        // Build a meta-prompt asking the LLM to improve the system prompt
        var refinementRequest = BuildRefinementPrompt(agent.SystemPrompt, negativeTasks, positiveTasks);

        LLMProvider? provider = agent.LLMProvider;
        if (provider == null)
            provider = await db.LLMProviders.FirstOrDefaultAsync(p => p.IsDefault && p.IsEnabled);
        if (provider == null)
            provider = await db.LLMProviders.FirstOrDefaultAsync(p => p.IsEnabled);
        if (provider == null)
            throw new InvalidOperationException("No LLM provider available.");

        // Wrap provider in a temporary agent stub for the execution service
        var stub = new Agent { LLMProvider = provider };
        var result = await _llm.ExecuteAsync(stub, refinementRequest, "You are an expert AI prompt engineer.");

        return new PromptRefinementSuggestion
        {
            AgentId          = agentId,
            CurrentPrompt    = agent.SystemPrompt,
            SuggestedPrompt  = result.Output,
            Reasoning        = "Generated from " + negativeTasks.Count + " negative + " + positiveTasks.Count + " positive feedback samples.",
            GeneratedAt      = DateTime.UtcNow,
            NegativeSamples  = negativeTasks.Count,
            PositiveSamples  = positiveTasks.Count
        };
    }

    /// <summary>
    /// Applies an approved prompt refinement — updates the agent's system prompt
    /// and records a new PromptHistory entry.
    /// </summary>
    public async Task ApplyRefinementAsync(string agentId, string newPrompt, string reason = "Self-learning refinement")
    {
        await using var db = _factory.CreateDbContext();

        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId)
            ?? throw new InvalidOperationException("Agent not found.");

        var stats = await GetStatsAsync(agentId);

        // Save new history entry
        var historyEntry = new PromptHistory
        {
            AgentId       = agentId,
            Prompt        = newPrompt,
            Version       = stats.CurrentVersion + 1,
            ChangedBy     = "SelfLearning",
            ChangeReason  = reason,
            SuccessRateAtChange = stats.SuccessRate,
            CreatedAt     = DateTime.UtcNow
        };

        db.PromptHistories.Add(historyEntry);
        agent.SystemPrompt = newPrompt;
        agent.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await _log.AddAsync(agentId, AgentLogLevel.Info,
            $"[SelfLearning] Prompt evolved to v{historyEntry.Version}. Success rate at change: {stats.SuccessRate:F1}%");
    }

    /// <summary>
    /// Stores episodic memory from a completed task so future runs can learn from it.
    /// </summary>
    public async Task RecordEpisodicMemoryAsync(string agentId, AgentTask task)
    {
        if (string.IsNullOrWhiteSpace(task.Output)) return;

        await using var db = _factory.CreateDbContext();

        var rating = task.FeedbackRating switch
        {
            1  => "positive",
            -1 => "negative",
            _  => "neutral"
        };

        var content = $"Task: {task.Name}\nInput: {task.Input}\nOutput: {task.Output}\nFeedback: {rating}";

        db.AgentMemories.Add(new AgentMemory
        {
            AgentId   = agentId,
            Type      = MemoryType.Episodic,
            Key       = $"task:{task.Id}",
            Value     = content.Length > 2000 ? content[..2000] : content,
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static string BuildRefinementPrompt(
        string currentPrompt,
        List<AgentTask> negative,
        List<AgentTask> positive)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are an expert AI prompt engineer. Your task is to improve an AI agent's system prompt based on feedback data.");
        sb.AppendLine();
        sb.AppendLine("## Current System Prompt");
        sb.AppendLine("```");
        sb.AppendLine(currentPrompt);
        sb.AppendLine("```");
        sb.AppendLine();

        if (positive.Any())
        {
            sb.AppendLine("## Successful Task Examples (rated positively)");
            foreach (var t in positive)
            {
                sb.AppendLine($"- Input: {Truncate(t.Input, 200)}");
                sb.AppendLine($"  Output: {Truncate(t.Output, 300)}");
            }
            sb.AppendLine();
        }

        if (negative.Any())
        {
            sb.AppendLine("## Failed/Poor Task Examples (rated negatively)");
            foreach (var t in negative)
            {
                sb.AppendLine($"- Input: {Truncate(t.Input, 200)}");
                sb.AppendLine($"  Output: {Truncate(t.Output, 300)}");
                if (!string.IsNullOrWhiteSpace(t.FeedbackNote))
                    sb.AppendLine($"  Feedback Note: {t.FeedbackNote}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Instructions");
        sb.AppendLine("Based on the above, write an improved version of the system prompt that:");
        sb.AppendLine("1. Preserves what works well (from positive examples)");
        sb.AppendLine("2. Addresses the failure patterns (from negative examples)");
        sb.AppendLine("3. Is clear, specific, and actionable");
        sb.AppendLine("4. Does NOT include meta-commentary — output ONLY the improved system prompt text.");

        return sb.ToString();
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        return text.Length <= max ? text : text[..max] + "…";
    }
}

// ── DTOs ────────────────────────────────────────────────────────────

public class AgentLearningStats
{
    public string AgentId { get; set; } = "";
    public int TotalTasks { get; set; }
    public int RatedTasks { get; set; }
    public int PositiveRatings { get; set; }
    public int NegativeRatings { get; set; }
    public double SuccessRate { get; set; }
    public int PromptVersions { get; set; }
    public int CurrentVersion { get; set; }
    public List<PromptHistory> PromptHistory { get; set; } = new();
    public List<AgentTask> RecentTasks { get; set; } = new();
}

public class PromptRefinementSuggestion
{
    public string AgentId { get; set; } = "";
    public string CurrentPrompt { get; set; } = "";
    public string SuggestedPrompt { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public DateTime GeneratedAt { get; set; }
    public int NegativeSamples { get; set; }
    public int PositiveSamples { get; set; }
}
