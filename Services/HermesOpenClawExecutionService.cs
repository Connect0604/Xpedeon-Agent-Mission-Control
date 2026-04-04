using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using XpedeonAgentMissionControl.Configuration;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class HermesOpenClawSubmitResult
{
    public bool Accepted { get; set; }
    public string ExternalRunId { get; set; } = string.Empty;
    public ExternalRunStatus Status { get; set; } = ExternalRunStatus.Submitted;
    public string? Trace { get; set; }
    public string? RawResponse { get; set; }
}

public class HermesOpenClawSyncResult
{
    public ExternalRunStatus Status { get; set; } = ExternalRunStatus.Running;
    public string? Output { get; set; }
    public string? ModelUsed { get; set; }
    public string? Trace { get; set; }
    public string? RawResponse { get; set; }
    public string? ErrorMessage { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public decimal CostUsd { get; set; }
    public int ToolCallsUsed { get; set; }
    public double? ConfidenceScore { get; set; }
}

public class HermesOpenClawExecutionService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HermesOpenClawConfig _config;

    public HermesOpenClawExecutionService(IHttpClientFactory httpClientFactory, HermesOpenClawConfig config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    public bool IsConfigured => _config.Enabled && !string.IsNullOrWhiteSpace(_config.BaseUrl);

    public async Task<HermesOpenClawSubmitResult> SubmitTaskAsync(Agent agent, AgentTask task, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var client = CreateClient();
        var payload = new
        {
            taskId = task.Id,
            taskName = task.Name,
            taskInput = task.Input,
            systemPrompt = task.SystemPromptSnapshot ?? agent.SystemPrompt,
            agent = new
            {
                id = agent.Id,
                name = agent.Name,
                type = agent.Type.ToString(),
                executionBackend = agent.ExecutionBackend.ToString()
            },
            provider = agent.LLMProvider == null ? null : new
            {
                id = agent.LLMProvider.Id,
                name = agent.LLMProvider.Name,
                modelName = agent.LLMProvider.ModelName,
                type = agent.LLMProvider.Type.ToString()
            },
            skills = agent.Skills
                .Where(s => s.IsEnabled && s.SkillDefinition != null)
                .Select(s => new
                {
                    id = s.SkillDefinitionId,
                    name = s.SkillDefinition!.Name,
                    description = s.SkillDefinition.Description,
                    promptSnippet = s.SkillDefinition.PromptSnippet
                })
                .ToList(),
            mcpServers = agent.MCPServers
                .Where(m => m.IsEnabled && m.MCPServer != null)
                .Select(m => new
                {
                    id = m.MCPServerId,
                    name = m.MCPServer!.Name,
                    endpoint = m.MCPServer.Endpoint,
                    transportType = m.MCPServer.TransportType.ToString()
                })
                .ToList(),
            metadata = new
            {
                createdAt = task.CreatedAt,
                priority = task.Priority.ToString(),
                replayOfTaskId = task.ReplayOfTaskId
            }
        };

        var response = await client.PostAsync(
            BuildAbsoluteUri(_config.SubmitPath),
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            cancellationToken);

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
        var root = document.RootElement;

        return new HermesOpenClawSubmitResult
        {
            Accepted = true,
            ExternalRunId = ReadString(root, "runId", "id", "jobId") ?? throw new InvalidOperationException("Hermes/OpenClaw submit response did not contain a run id."),
            Status = ParseExternalStatus(ReadString(root, "status")),
            Trace = ReadString(root, "trace", "message"),
            RawResponse = raw
        };
    }

    public async Task<HermesOpenClawSyncResult> GetRunStatusAsync(string externalRunId, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var client = CreateClient();
        var response = await client.GetAsync(BuildAbsoluteUri(_config.StatusPathTemplate.Replace("{runId}", externalRunId)), cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
        var root = document.RootElement;

        var promptTokens = ReadInt(root, "promptTokens", "prompt_tokens");
        var completionTokens = ReadInt(root, "completionTokens", "completion_tokens");
        var totalTokens = ReadInt(root, "totalTokens", "total_tokens");
        if (totalTokens == 0)
            totalTokens = promptTokens + completionTokens;

        return new HermesOpenClawSyncResult
        {
            Status = ParseExternalStatus(ReadString(root, "status")),
            Output = ReadString(root, "output", "result", "content"),
            ModelUsed = ReadString(root, "modelUsed", "model"),
            Trace = ReadString(root, "trace", "executionTrace", "message"),
            RawResponse = raw,
            ErrorMessage = ReadString(root, "error", "errorMessage"),
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = totalTokens,
            CostUsd = ReadDecimal(root, "costUsd", "cost_usd"),
            ToolCallsUsed = ReadInt(root, "toolCallsUsed", "tool_calls_used"),
            ConfidenceScore = ReadDoubleNullable(root, "confidenceScore", "confidence")
        };
    }

    public async Task CancelRunAsync(string externalRunId, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(externalRunId))
            return;

        var client = CreateClient();
        var response = await client.PostAsync(BuildAbsoluteUri(_config.CancelPathTemplate.Replace("{runId}", externalRunId)), null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public static bool IsTerminal(ExternalRunStatus status)
        => status is ExternalRunStatus.Completed or ExternalRunStatus.Failed or ExternalRunStatus.Cancelled;

    public static ExternalRunStatus ParseExternalStatus(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "submitted" or "queued" => ExternalRunStatus.Submitted,
            "running" or "in_progress" or "processing" => ExternalRunStatus.Running,
            "completed" or "succeeded" or "success" => ExternalRunStatus.Completed,
            "failed" or "error" => ExternalRunStatus.Failed,
            "cancelled" or "canceled" => ExternalRunStatus.Cancelled,
            _ => ExternalRunStatus.Running
        };
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient();
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        return client;
    }

    private string BuildAbsoluteUri(string path)
        => $"{_config.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Hermes/OpenClaw runtime is not configured. Set HermesOpenClaw.Enabled and BaseUrl in appsettings.");
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
                return element.GetString();
        }

        return null;
    }

    private static int ReadInt(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var element) && element.TryGetInt32(out var value))
                return value;
        }

        return 0;
    }

    private static decimal ReadDecimal(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var element))
            {
                if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var number))
                    return number;
                if (element.ValueKind == JsonValueKind.String && decimal.TryParse(element.GetString(), out var parsed))
                    return parsed;
            }
        }

        return 0;
    }

    private static double? ReadDoubleNullable(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var element))
            {
                if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number))
                    return number;
                if (element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), out var parsed))
                    return parsed;
            }
        }

        return null;
    }
}
