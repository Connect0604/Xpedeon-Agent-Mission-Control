using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class ProviderConnectionTestResult
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = string.Empty;
}

public class LLMProviderService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IHttpClientFactory _httpFactory;

    public LLMProviderService(IDbContextFactory<AppDbContext> factory, IHttpClientFactory httpFactory)
    {
        _factory = factory;
        _httpFactory = httpFactory;
    }

    public async Task<List<LLMProvider>> GetAllAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.LLMProviders.OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).ToListAsync();
    }

    public async Task<LLMProvider?> GetByIdAsync(string id)
    {
        await using var db = _factory.CreateDbContext();
        return await db.LLMProviders.FindAsync(id);
    }

    public async Task<LLMProvider?> GetDefaultAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.LLMProviders.FirstOrDefaultAsync(p => p.IsDefault && p.IsEnabled);
    }

    public async Task<LLMProvider> CreateAsync(LLMProvider provider)
    {
        await using var db = _factory.CreateDbContext();

        if (provider.IsDefault)
        {
            // Clear existing defaults
            var existing = await db.LLMProviders.Where(p => p.IsDefault).ToListAsync();
            existing.ForEach(p => p.IsDefault = false);
        }

        provider.Id = Guid.NewGuid().ToString();
        provider.CreatedAt = DateTime.UtcNow;
        db.LLMProviders.Add(provider);
        await db.SaveChangesAsync();
        return provider;
    }

    public async Task<LLMProvider> UpdateAsync(LLMProvider provider)
    {
        await using var db = _factory.CreateDbContext();

        if (provider.IsDefault)
        {
            var existing = await db.LLMProviders.Where(p => p.IsDefault && p.Id != provider.Id).ToListAsync();
            existing.ForEach(p => p.IsDefault = false);
        }

        db.LLMProviders.Update(provider);
        await db.SaveChangesAsync();
        return provider;
    }

    public async Task DeleteAsync(string id)
    {
        await using var db = _factory.CreateDbContext();
        var provider = await db.LLMProviders.FindAsync(id);
        if (provider != null)
        {
            db.LLMProviders.Remove(provider);
            await db.SaveChangesAsync();
        }
    }

    public async Task<ProviderConnectionTestResult> TestConnectionAsync(LLMProvider provider)
    {
        try
        {
            return provider.Type switch
            {
                LLMProviderType.Ollama => await TestOllamaConnectionAsync(provider),
                LLMProviderType.OpenAI => await TestOpenAIConnectionAsync(provider),
                LLMProviderType.Claude => await TestClaudeConnectionAsync(provider),
                LLMProviderType.Custom => await TestCustomConnectionAsync(provider),
                _ => new ProviderConnectionTestResult { IsSuccess = false, Message = "Provider type is not supported." }
            };
        }
        catch (TaskCanceledException)
        {
            return new ProviderConnectionTestResult
            {
                IsSuccess = false,
                Message = "Connection timed out. Check the endpoint and network access."
            };
        }
        catch (HttpRequestException ex)
        {
            return new ProviderConnectionTestResult
            {
                IsSuccess = false,
                Message = $"Connection failed: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            return new ProviderConnectionTestResult
            {
                IsSuccess = false,
                Message = $"Connection test failed: {ex.Message}"
            };
        }
    }

    private async Task<ProviderConnectionTestResult> TestOllamaConnectionAsync(LLMProvider provider)
    {
        var endpoint = string.IsNullOrWhiteSpace(provider.Endpoint)
            ? "http://localhost:11434"
            : provider.Endpoint.TrimEnd('/');

        var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);

        var response = await client.GetAsync($"{endpoint}/api/tags");
        if (!response.IsSuccessStatusCode)
            return await BuildFailureAsync(response, "Ollama server check failed");

        if (string.IsNullOrWhiteSpace(provider.ModelName))
        {
            return new ProviderConnectionTestResult
            {
                IsSuccess = true,
                Message = "Ollama server is reachable."
            };
        }

        var payload = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(payload);
        if (!json.RootElement.TryGetProperty("models", out var models))
        {
            return new ProviderConnectionTestResult
            {
                IsSuccess = false,
                Message = "Ollama responded, but the models list could not be read."
            };
        }

        var requestedModel = provider.ModelName.Trim();
        var found = models.EnumerateArray()
            .Select(m => m.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Any(name => string.Equals(name, requestedModel, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, $"{requestedModel}:latest", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name?.Split(':')[0], requestedModel, StringComparison.OrdinalIgnoreCase));

        return found
            ? new ProviderConnectionTestResult
            {
                IsSuccess = true,
                Message = $"Ollama is reachable and model '{requestedModel}' is available."
            }
            : new ProviderConnectionTestResult
            {
                IsSuccess = false,
                Message = $"Ollama is reachable, but model '{requestedModel}' was not found."
            };
    }

    private async Task<ProviderConnectionTestResult> TestOpenAIConnectionAsync(LLMProvider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            return new ProviderConnectionTestResult { IsSuccess = false, Message = "API key is required." };
        if (string.IsNullOrWhiteSpace(provider.ModelName))
            return new ProviderConnectionTestResult { IsSuccess = false, Message = "Model name is required." };

        var endpoint = string.IsNullOrWhiteSpace(provider.Endpoint)
            ? "https://api.openai.com/v1/chat/completions"
            : provider.Endpoint;

        var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(12);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);

        var body = new
        {
            model = provider.ModelName,
            max_tokens = 1,
            temperature = 0,
            messages = new[] { new { role = "user", content = "Reply with OK." } }
        };

        var response = await client.PostAsync(endpoint,
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

        return response.IsSuccessStatusCode
            ? new ProviderConnectionTestResult
            {
                IsSuccess = true,
                Message = $"OpenAI connection succeeded for model '{provider.ModelName}'."
            }
            : await BuildFailureAsync(response, "OpenAI connection failed");
    }

    private async Task<ProviderConnectionTestResult> TestClaudeConnectionAsync(LLMProvider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            return new ProviderConnectionTestResult { IsSuccess = false, Message = "API key is required." };
        if (string.IsNullOrWhiteSpace(provider.ModelName))
            return new ProviderConnectionTestResult { IsSuccess = false, Message = "Model name is required." };

        var endpoint = string.IsNullOrWhiteSpace(provider.Endpoint)
            ? "https://api.anthropic.com/v1/messages"
            : provider.Endpoint;

        var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(12);
        client.DefaultRequestHeaders.Add("x-api-key", provider.ApiKey);
        client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var body = new
        {
            model = provider.ModelName,
            max_tokens = 1,
            temperature = 0,
            messages = new[] { new { role = "user", content = "Reply with OK." } }
        };

        var response = await client.PostAsync(endpoint,
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

        return response.IsSuccessStatusCode
            ? new ProviderConnectionTestResult
            {
                IsSuccess = true,
                Message = $"Claude connection succeeded for model '{provider.ModelName}'."
            }
            : await BuildFailureAsync(response, "Claude connection failed");
    }

    private async Task<ProviderConnectionTestResult> TestCustomConnectionAsync(LLMProvider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Endpoint))
            return new ProviderConnectionTestResult { IsSuccess = false, Message = "Endpoint URL is required." };
        if (string.IsNullOrWhiteSpace(provider.ModelName))
            return new ProviderConnectionTestResult { IsSuccess = false, Message = "Model name is required." };

        var token = !string.IsNullOrWhiteSpace(provider.AuthToken) ? provider.AuthToken : provider.ApiKey;
        if (string.IsNullOrWhiteSpace(token))
            return new ProviderConnectionTestResult { IsSuccess = false, Message = "Auth token is required." };

        var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(12);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var body = new
        {
            model = provider.ModelName,
            max_tokens = 1,
            temperature = 0,
            messages = new[] { new { role = "user", content = "Reply with OK." } }
        };

        var response = await client.PostAsync(provider.Endpoint,
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

        return response.IsSuccessStatusCode
            ? new ProviderConnectionTestResult
            {
                IsSuccess = true,
                Message = $"Custom provider connection succeeded for model '{provider.ModelName}'."
            }
            : await BuildFailureAsync(response, "Custom provider connection failed");
    }

    private static async Task<ProviderConnectionTestResult> BuildFailureAsync(HttpResponseMessage response, string prefix)
    {
        var body = await response.Content.ReadAsStringAsync();
        var detail = string.IsNullOrWhiteSpace(body)
            ? response.ReasonPhrase ?? "Unknown error"
            : body.Trim().ReplaceLineEndings(" ");

        if (detail.Length > 180)
            detail = detail[..180] + "...";

        return new ProviderConnectionTestResult
        {
            IsSuccess = false,
            Message = $"{prefix}: {(int)response.StatusCode} {detail}"
        };
    }
}
