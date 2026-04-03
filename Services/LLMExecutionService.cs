using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class LLMResult
{
    public string Output { get; set; } = string.Empty;
    public string Trace { get; set; } = string.Empty;
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public decimal CostUSD { get; set; }
    public string ModelUsed { get; set; } = string.Empty;
    public double ConfidenceScore { get; set; } = 1.0;
}

public class LLMExecutionService
{
    private const int MaxMcpIterations = 4;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly MCPService _mcpService;
    private readonly ILogger<LLMExecutionService> _logger;

    public LLMExecutionService(
        IHttpClientFactory httpFactory,
        IDbContextFactory<AppDbContext> dbFactory,
        MCPService mcpService,
        ILogger<LLMExecutionService> logger)
    {
        _httpFactory = httpFactory;
        _dbFactory = dbFactory;
        _mcpService = mcpService;
        _logger = logger;
    }

    public async Task<LLMResult> ExecuteAsync(Agent agent, string userInput, string systemPrompt, CancellationToken cancellationToken = default)
    {
        var provider = agent.LLMProvider;
        if (provider == null)
            throw new InvalidOperationException("Agent has no LLM provider configured.");

        var assignedMcp = agent.MCPServers
            .Where(m => m.IsEnabled && m.MCPServer is { IsEnabled: true })
            .Select(m => m.MCPServer!)
            .ToList();

        var globalMcp = await GetGlobalMcpServersAsync(agent.Id, cancellationToken);
        var allMcp = assignedMcp
            .Concat(globalMcp)
            .GroupBy(s => s.Id)
            .Select(g => g.First())
            .ToList();

        var discoveredTools = allMcp.Any()
            ? await _mcpService.ListToolsAsync(allMcp, cancellationToken)
            : new List<MCPToolInfo>();

        var effectiveSystemPrompt = BuildEffectiveSystemPrompt(systemPrompt, allMcp, discoveredTools);
        var currentInput = userInput;
        var aggregatedToolResults = new List<MCPToolCallResult>();
        var traceParts = new List<string>();
        LLMResult? latestResult = null;

        for (var iteration = 1; iteration <= MaxMcpIterations; iteration++)
        {
            var iterationPrompt = iteration == 1
                ? effectiveSystemPrompt
                : $"{effectiveSystemPrompt}\n\nUse real MCP tool results when answering. Do not emit <function_calls> markup in the final response unless you truly need another tool.";

            latestResult = await ExecuteProviderAsync(provider, iterationPrompt, currentInput, cancellationToken);
            traceParts.Add(latestResult.Trace);

            var requestedCalls = ParseToolCalls(latestResult.Output);
            if (!requestedCalls.Any())
            {
                latestResult.Trace = string.Join(Environment.NewLine, traceParts);
                return latestResult;
            }

            var iterationResults = new List<MCPToolCallResult>();
            foreach (var call in requestedCalls)
            {
                var tool = discoveredTools.FirstOrDefault(t => t.Name.Equals(call.ToolName, StringComparison.OrdinalIgnoreCase));
                if (tool == null)
                {
                    iterationResults.Add(new MCPToolCallResult
                    {
                        Success = false,
                        ToolName = call.ToolName,
                        ResultText = $"Requested MCP tool '{call.ToolName}' was not found in the discovered tool list.",
                        Trace = $"MCP tool not found: {call.ToolName}"
                    });
                    continue;
                }

                var server = allMcp.FirstOrDefault(s => s.Id == tool.ServerId);
                if (server == null)
                {
                    iterationResults.Add(new MCPToolCallResult
                    {
                        Success = false,
                        ToolName = call.ToolName,
                        ServerName = tool.ServerName,
                        ResultText = $"No MCP server mapping was found for tool '{call.ToolName}'.",
                        Trace = $"MCP server not found for tool: {call.ToolName}"
                    });
                    continue;
                }

                var result = await _mcpService.InvokeToolAsync(server, tool.Name, call.Arguments, cancellationToken);
                iterationResults.Add(result);
            }

            aggregatedToolResults.AddRange(iterationResults);
            traceParts.AddRange(iterationResults.Select(r => r.Trace));

            currentInput = BuildToolResultFollowUpInput(userInput, aggregatedToolResults);
        }

        if (latestResult == null)
            throw new InvalidOperationException("LLM execution did not produce a result.");

        latestResult.Output = $"{StripFunctionCalls(latestResult.Output)}\n\n[MCP tool loop stopped after {MaxMcpIterations} iterations.]".Trim();
        latestResult.Trace = string.Join(Environment.NewLine, traceParts);
        return latestResult;
    }

    private Task<LLMResult> ExecuteProviderAsync(LLMProvider provider, string systemPrompt, string userInput, CancellationToken cancellationToken)
        => provider.Type switch
        {
            LLMProviderType.Claude => ExecuteClaudeAsync(provider, systemPrompt, userInput, cancellationToken),
            LLMProviderType.OpenAI => ExecuteOpenAIAsync(provider, systemPrompt, userInput, cancellationToken),
            LLMProviderType.Ollama => ExecuteOllamaAsync(provider, systemPrompt, userInput, cancellationToken),
            LLMProviderType.Custom => ExecuteCustomAsync(provider, systemPrompt, userInput, cancellationToken),
            _ => throw new NotSupportedException($"Provider type {provider.Type} not supported")
        };

    // ── Claude (Anthropic) ───────────────────────────────────────────────────

    private async Task<LLMResult> ExecuteClaudeAsync(LLMProvider provider, string systemPrompt, string userInput, CancellationToken cancellationToken)
    {
        var client = _httpFactory.CreateClient();
        var endpoint = string.IsNullOrWhiteSpace(provider.Endpoint)
            ? "https://api.anthropic.com/v1/messages"
            : provider.Endpoint;

        client.DefaultRequestHeaders.Add("x-api-key", provider.ApiKey);
        client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var body = new
        {
            model = provider.ModelName,
            max_tokens = provider.MaxTokens,
            temperature = provider.Temperature,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = userInput } }
        };

        var response = await client.PostAsync(endpoint,
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        var output = root.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
        var usage = root.GetProperty("usage");
        var promptTokens = usage.GetProperty("input_tokens").GetInt32();
        var completionTokens = usage.GetProperty("output_tokens").GetInt32();

        return new LLMResult
        {
            Output = output,
            ModelUsed = provider.ModelName,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = promptTokens + completionTokens,
            CostUSD = CalculateCost(provider.ModelName, promptTokens, completionTokens),
            Trace = $"Claude API call — model: {provider.ModelName}, input: {promptTokens} tokens, output: {completionTokens} tokens"
        };
    }

    // ── OpenAI ───────────────────────────────────────────────────────────────

    private async Task<LLMResult> ExecuteOpenAIAsync(LLMProvider provider, string systemPrompt, string userInput, CancellationToken cancellationToken)
    {
        var client = _httpFactory.CreateClient();
        var endpoint = string.IsNullOrWhiteSpace(provider.Endpoint)
            ? "https://api.openai.com/v1/chat/completions"
            : provider.Endpoint;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);

        var body = new
        {
            model = provider.ModelName,
            max_tokens = provider.MaxTokens,
            temperature = provider.Temperature,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userInput }
            }
        };

        var response = await client.PostAsync(endpoint,
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        var output = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
        var usage = root.GetProperty("usage");
        var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
        var completionTokens = usage.GetProperty("completion_tokens").GetInt32();

        return new LLMResult
        {
            Output = output,
            ModelUsed = provider.ModelName,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = promptTokens + completionTokens,
            CostUSD = CalculateCost(provider.ModelName, promptTokens, completionTokens),
            Trace = $"OpenAI API call — model: {provider.ModelName}, input: {promptTokens} tokens, output: {completionTokens} tokens"
        };
    }

    // ── Ollama (local) ───────────────────────────────────────────────────────

    private async Task<LLMResult> ExecuteOllamaAsync(LLMProvider provider, string systemPrompt, string userInput, CancellationToken cancellationToken)
    {
        var client = _httpFactory.CreateClient();
        var endpoint = string.IsNullOrWhiteSpace(provider.Endpoint)
            ? "http://localhost:11434/api/chat"
            : $"{provider.Endpoint.TrimEnd('/')}/api/chat";

        var body = new
        {
            model = provider.ModelName,
            stream = false,
            options = new { temperature = provider.Temperature },
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userInput }
            }
        };

        var response = await client.PostAsync(endpoint,
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        var output = root.GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
        var promptTokens = root.TryGetProperty("prompt_eval_count", out var pt) ? pt.GetInt32() : 0;
        var completionTokens = root.TryGetProperty("eval_count", out var ct) ? ct.GetInt32() : 0;

        return new LLMResult
        {
            Output = output,
            ModelUsed = provider.ModelName,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = promptTokens + completionTokens,
            CostUSD = 0, // Ollama is free/local
            Trace = $"Ollama local call — model: {provider.ModelName}, endpoint: {endpoint}"
        };
    }

    // ── Custom ───────────────────────────────────────────────────────────────

    private async Task<LLMResult> ExecuteCustomAsync(LLMProvider provider, string systemPrompt, string userInput, CancellationToken cancellationToken)
    {
        var client = _httpFactory.CreateClient();
        if (!string.IsNullOrWhiteSpace(provider.AuthToken))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", provider.AuthToken);

        // Uses OpenAI-compatible format
        var body = new
        {
            model = provider.ModelName,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userInput }
            },
            temperature = provider.Temperature,
            max_tokens = provider.MaxTokens
        };

        var response = await client.PostAsync(provider.Endpoint,
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        var output = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;

        int promptTokens = 0, completionTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            promptTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
            completionTokens = usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;
        }

        return new LLMResult
        {
            Output = output,
            ModelUsed = provider.ModelName,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = promptTokens + completionTokens,
            CostUSD = 0,
            Trace = $"Custom API call — endpoint: {provider.Endpoint}, model: {provider.ModelName}"
        };
    }

    // ── Cost Estimation ───────────────────────────────────────────────────────

    private static decimal CalculateCost(string model, int promptTokens, int completionTokens)
    {
        // Prices per 1M tokens (approximate, USD)
        var (inputPer1M, outputPer1M) = model.ToLower() switch
        {
            var m when m.Contains("claude-opus")   => (15.0m, 75.0m),
            var m when m.Contains("claude-sonnet") => (3.0m,  15.0m),
            var m when m.Contains("claude-haiku")  => (0.25m, 1.25m),
            var m when m.Contains("gpt-4o")        => (5.0m,  15.0m),
            var m when m.Contains("gpt-4")         => (10.0m, 30.0m),
            var m when m.Contains("gpt-3.5")       => (0.5m,  1.5m),
            _ => (1.0m, 3.0m) // default estimate
        };

        return (promptTokens / 1_000_000m * inputPer1M) + (completionTokens / 1_000_000m * outputPer1M);
    }

    private static string BuildEffectiveSystemPrompt(string systemPrompt, IReadOnlyCollection<MCPServer> servers, IReadOnlyCollection<MCPToolInfo> tools)
    {
        if (!servers.Any())
            return systemPrompt;

        var builder = new StringBuilder(systemPrompt.Trim());
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("Available MCP servers:");

        foreach (var server in servers)
        {
            builder.Append("- ");
            builder.Append(server.Name);
            builder.Append(" [");
            builder.Append(server.TransportType);
            builder.Append("] ");
            if (!string.IsNullOrWhiteSpace(server.Description))
            {
                builder.Append(server.Description.Trim());
                builder.Append(" ");
            }
            builder.Append("Endpoint: ");
            builder.Append(server.Endpoint);
            builder.AppendLine();
        }

        if (tools.Any())
        {
            builder.AppendLine();
            builder.AppendLine("Discovered MCP tools:");
            foreach (var tool in tools)
            {
                builder.Append("- ");
                builder.Append(tool.Name);
                builder.Append(" [");
                builder.Append(tool.ServerName);
                builder.Append(']');
                if (!string.IsNullOrWhiteSpace(tool.Description))
                {
                    builder.Append(" ");
                    builder.Append(tool.Description.Trim());
                }
                builder.AppendLine();
                if (!string.IsNullOrWhiteSpace(tool.InputSchemaJson))
                {
                    builder.Append("  Input schema: ");
                    builder.AppendLine(tool.InputSchemaJson);
                }
            }

            builder.AppendLine();
            builder.AppendLine("When you need a tool, emit exactly this XML format:");
            builder.AppendLine("<function_calls>");
            builder.AppendLine("<invoke name=\"tool_name\">");
            builder.AppendLine("<parameter name=\"argument_name\">value</parameter>");
            builder.AppendLine("</invoke>");
            builder.AppendLine("</function_calls>");
            builder.AppendLine("Only call tools from the discovered list above.");
            builder.AppendLine("Use exact parameter names expected by the tool input schema.");

            var knownExamples = BuildKnownToolExamples(tools);
            if (!string.IsNullOrWhiteSpace(knownExamples))
            {
                builder.AppendLine();
                builder.AppendLine("Tool usage examples:");
                builder.AppendLine(knownExamples.Trim());
            }
        }

        return builder.ToString();
    }

    private static string BuildToolResultFollowUpInput(string userInput, IEnumerable<MCPToolCallResult> toolResults)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Original user request:");
        builder.AppendLine(userInput.Trim());
        builder.AppendLine();
        builder.AppendLine("Real MCP tool results:");

        foreach (var result in toolResults)
        {
            builder.Append("- Tool: ");
            builder.Append(result.ToolName);
            builder.Append(" | Server: ");
            builder.AppendLine(result.ServerName);
            builder.AppendLine(result.ResultText);
            builder.AppendLine();
        }

        builder.AppendLine("Answer the user using the tool results above.");
        return builder.ToString();
    }

    private static string StripFunctionCalls(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return output;

        var cleaned = Regex.Replace(output, "<function_calls>.*?</function_calls>", string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return cleaned.Trim();
    }

    private async Task<List<MCPServer>> GetGlobalMcpServersAsync(string? agentId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return new List<MCPServer>();

        await using var db = _dbFactory.CreateDbContext();
        return await db.MCPServers
            .Where(s => s.IsEnabled && s.IsGlobal)
            .OrderBy(s => s.Name)
            .ToListAsync(cancellationToken);
    }

    private static List<MCPToolCallRequest> ParseToolCalls(string output)
    {
        var requests = new List<MCPToolCallRequest>();
        if (string.IsNullOrWhiteSpace(output) || !output.Contains("<invoke", StringComparison.OrdinalIgnoreCase))
            return requests;

        var invokeMatches = Regex.Matches(output,
            "<invoke\\s+name=\"(?<name>[^\"]+)\"\\s*>(?<body>.*?)</invoke>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match invokeMatch in invokeMatches)
        {
            var toolName = invokeMatch.Groups["name"].Value;
            if (string.IsNullOrWhiteSpace(toolName))
                continue;

            var arguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var body = invokeMatch.Groups["body"].Value;
            var parameterMatches = Regex.Matches(body,
                "<parameter\\s+name=\"(?<name>[^\"]+)\"\\s*>(?<value>.*?)</parameter>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            foreach (Match parameterMatch in parameterMatches)
            {
                var parameterName = parameterMatch.Groups["name"].Value;
                var parameterValue = System.Net.WebUtility.HtmlDecode(parameterMatch.Groups["value"].Value.Trim());
                if (!string.IsNullOrWhiteSpace(parameterName))
                    arguments[parameterName] = parameterValue;
            }

            requests.Add(new MCPToolCallRequest
            {
                ToolName = toolName,
                Arguments = arguments
            });
        }

        return requests;
    }

    private static string BuildKnownToolExamples(IReadOnlyCollection<MCPToolInfo> tools)
    {
        var builder = new StringBuilder();

        if (tools.Any(t => t.Name.Equals("xpedeon-database-tool", StringComparison.OrdinalIgnoreCase)))
        {
            builder.AppendLine("- For xpedeon-database-tool table definition requests, use:");
            builder.AppendLine("  <function_calls>");
            builder.AppendLine("  <invoke name=\"xpedeon-database-tool\">");
            builder.AppendLine("  <parameter name=\"operation\">table-definition</parameter>");
            builder.AppendLine("  <parameter name=\"objectName\">dbo.pc_process_steps_default</parameter>");
            builder.AppendLine("  </invoke>");
            builder.AppendLine("  </function_calls>");
        }

        if (tools.Any(t => t.Name.Equals("xpedeon-database-legacy-tool", StringComparison.OrdinalIgnoreCase)))
        {
            builder.AppendLine("- For xpedeon-database-legacy-tool table definition requests, use:");
            builder.AppendLine("  <function_calls>");
            builder.AppendLine("  <invoke name=\"xpedeon-database-legacy-tool\">");
            builder.AppendLine("  <parameter name=\"operation\">table-definition</parameter>");
            builder.AppendLine("  <parameter name=\"objectName\">dbo.pc_process_steps_default</parameter>");
            builder.AppendLine("  </invoke>");
            builder.AppendLine("  </function_calls>");
        }

        return builder.ToString();
    }

    private sealed class MCPToolCallRequest
    {
        public string ToolName { get; set; } = string.Empty;
        public Dictionary<string, object?> Arguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
