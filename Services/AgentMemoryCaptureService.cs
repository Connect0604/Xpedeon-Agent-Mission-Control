using System.Text;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class AgentMemoryCaptureService
{
    private readonly MemoryService _memoryService;

    public AgentMemoryCaptureService(MemoryService memoryService)
    {
        _memoryService = memoryService;
    }

    public async Task CaptureCompletedTaskMemoriesAsync(Agent agent, AgentTask task)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(task);

        if (string.IsNullOrWhiteSpace(task.Output))
            return;

        if (!IsAnyMemoryModeEnabled(agent))
            return;

        var summaryContent = BuildSummaryContent(task);

        if (agent.ShortTermMemoryEnabled)
        {
            await _memoryService.StoreAsync(
                agent.Id,
                $"task:{task.Id}:short-term",
                summaryContent,
                MemoryType.ShortTerm,
                swarmId: agent.SwarmId,
                taskId: task.Id);
        }

        if (agent.LongTermMemoryEnabled)
        {
            await _memoryService.StoreAsync(
                agent.Id,
                $"task:{task.Id}:long-term",
                summaryContent,
                MemoryType.LongTerm,
                swarmId: agent.SwarmId,
                taskId: task.Id);
        }

        if (agent.ShareMemoryWithSwarm && !string.IsNullOrWhiteSpace(agent.SwarmId))
        {
            await _memoryService.StoreAsync(
                agent.Id,
                $"task:{task.Id}:swarm",
                summaryContent,
                MemoryType.Swarm,
                swarmId: agent.SwarmId,
                taskId: task.Id);
        }

        await _memoryService.StoreAsync(
            agent.Id,
            $"task:{task.Id}:episodic",
            BuildEpisodicContent(task),
            MemoryType.Episodic,
            swarmId: agent.SwarmId,
            taskId: task.Id);
    }

    private static bool IsAnyMemoryModeEnabled(Agent agent)
        => agent.ShortTermMemoryEnabled || agent.LongTermMemoryEnabled || agent.ShareMemoryWithSwarm;

    private static string BuildSummaryContent(AgentTask task)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Task: {task.Name}");
        AppendIfPresent(builder, "Input", task.Input);
        AppendIfPresent(builder, "Output", task.Output);
        AppendIfPresent(builder, "Model", task.ModelUsed);
        if (task.TotalTokens > 0)
            builder.AppendLine($"Tokens: {task.TotalTokens}");
        if (task.CostUSD > 0)
            builder.AppendLine($"CostUSD: {task.CostUSD:F6}");

        return TrimToLimit(builder.ToString());
    }

    private static string BuildEpisodicContent(AgentTask task)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Task: {task.Name}");
        AppendIfPresent(builder, "Input", task.Input);
        AppendIfPresent(builder, "Output", task.Output);
        builder.AppendLine($"Feedback: {GetFeedbackTone(task.FeedbackRating)}");
        AppendIfPresent(builder, "Feedback Note", task.FeedbackNote);
        return TrimToLimit(builder.ToString());
    }

    private static void AppendIfPresent(StringBuilder builder, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            builder.AppendLine($"{label}: {value}");
    }

    private static string GetFeedbackTone(int? rating)
    {
        if (rating is >= 4)
            return "positive";
        if (rating is <= 2)
            return "negative";
        return "neutral";
    }

    private static string TrimToLimit(string value)
        => value.Length > 2000 ? value[..2000] : value;
}
