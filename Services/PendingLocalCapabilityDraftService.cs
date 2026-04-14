using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public sealed class PendingLocalCapabilityDraftService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public PendingLocalCapabilityDraftService(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<List<PendingLocalCapabilityDraft>> GetPendingDraftsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var tasks = await db.Tasks
            .Where(task => task.Status == AgentTaskStatus.PendingApproval && !string.IsNullOrWhiteSpace(task.LocalCapabilityDraftJson))
            .OrderByDescending(task => task.CreatedAt)
            .ToListAsync(cancellationToken);

        if (tasks.Count == 0)
        {
            return new List<PendingLocalCapabilityDraft>();
        }

        var agentIds = tasks
            .Select(task => task.AgentId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allowedRootsByAgent = await db.Agents
            .Where(agent => agentIds.Contains(agent.Id))
            .ToDictionaryAsync(agent => agent.Id, agent => agent.AllowedLocalRootsJson, cancellationToken);

        var drafts = new List<PendingLocalCapabilityDraft>();
        foreach (var task in tasks)
        {
            var parsed = TryParseDraft(task.LocalCapabilityDraftJson!);
            if (parsed is null)
            {
                continue;
            }

            drafts.Add(new PendingLocalCapabilityDraft
            {
                TaskId = task.Id,
                AgentId = task.AgentId,
                AgentName = task.AgentName,
                TaskName = task.Name,
                CreatedAt = task.CreatedAt,
                Summary = parsed.Summary,
                DraftName = parsed.DraftName,
                DraftDescription = parsed.DraftDescription,
                DraftCategory = parsed.DraftCategory,
                DraftExecutionType = parsed.DraftExecutionType,
                DraftInputsJson = parsed.DraftInputsJson,
                InvocationInputsJson = parsed.InvocationInputsJson,
                ScriptPath = parsed.ScriptPath,
                Script = parsed.Script,
                AllowedRootsJson = allowedRootsByAgent.TryGetValue(task.AgentId, out var allowedRoots) ? allowedRoots : null,
                ApprovalEvidence = task.ApprovalEvidence
            });
        }

        return drafts;
    }

    private static ParsedDraft? TryParseDraft(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;

            if (!root.TryGetProperty("mode", out var modeElement) ||
                !string.Equals(modeElement.GetString(), "draftCapability", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!root.TryGetProperty("draft", out var draftElement))
            {
                return null;
            }

            var summary = root.TryGetProperty("summary", out var summaryElement)
                ? summaryElement.GetString() ?? string.Empty
                : string.Empty;
            var scriptPath = root.TryGetProperty("draftScriptPath", out var scriptPathElement)
                ? scriptPathElement.GetString() ?? string.Empty
                : string.Empty;
            var invocationInputsJson = root.TryGetProperty("inputs", out var inputsElement)
                ? inputsElement.GetRawText()
                : "{}";

            return new ParsedDraft(
                summary,
                draftElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty,
                draftElement.TryGetProperty("description", out var descriptionElement) ? descriptionElement.GetString() ?? string.Empty : string.Empty,
                draftElement.TryGetProperty("category", out var categoryElement) ? categoryElement.GetString() ?? string.Empty : string.Empty,
                draftElement.TryGetProperty("executionType", out var executionTypeElement) ? executionTypeElement.GetString() ?? string.Empty : string.Empty,
                draftElement.TryGetProperty("inputs", out var draftInputsElement) ? draftInputsElement.GetRawText() : "{}",
                draftElement.TryGetProperty("script", out var scriptElement) ? scriptElement.GetString() ?? string.Empty : string.Empty,
                invocationInputsJson,
                scriptPath);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ParsedDraft(
        string Summary,
        string DraftName,
        string DraftDescription,
        string DraftCategory,
        string DraftExecutionType,
        string DraftInputsJson,
        string Script,
        string InvocationInputsJson,
        string ScriptPath);
}
