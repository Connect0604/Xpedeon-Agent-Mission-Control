using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<LLMExecutionService> _logger;

    public LLMExecutionService(IHttpClientFactory httpFactory, ILogger<LLMExecutionService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<LLMResult> ExecuteAsync(Agent agent, string userInput, string systemPrompt)
    {
        var provider = agent.LLMProvider;
        if (provider == null)
            throw new InvalidOperationException("Agent has no LLM provider configured.");

        return provider.Type switch
        {
            LLMProviderType.Claude  => await ExecuteClaudeAsync(provider, systemPrompt, userInput),
            LLMProviderType.OpenAI  => await ExecuteOpenAIAsync(provider, systemPrompt, userInput),
            LLMProviderType.Ollama  => await ExecuteOllamaAsync(provider, systemPrompt, userInput),
            LLMProviderType.Custom  => await ExecuteCustomAsync(provider, systemPrompt, userInput),
            _ => throw new NotSupportedException($"Provider type {provider.Type} not supported")
        };
    }

    // ── Claude (Anthropic) ───────────────────────────────────────────────────

    private async Task<LLMResult> ExecuteClaudeAsync(LLMProvider provider, string systemPrompt, string userInput)
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
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
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

    private async Task<LLMResult> ExecuteOpenAIAsync(LLMProvider provider, string systemPrompt, string userInput)
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
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
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

    private async Task<LLMResult> ExecuteOllamaAsync(LLMProvider provider, string systemPrompt, string userInput)
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
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
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

    private async Task<LLMResult> ExecuteCustomAsync(LLMProvider provider, string systemPrompt, string userInput)
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
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
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
}
