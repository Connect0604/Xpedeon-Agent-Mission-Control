namespace XpedeonAgentMissionControl.Models;

public enum LLMProviderType { Claude, OpenAI, Ollama, Custom }

public class LLMProvider
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public LLMProviderType Type { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? AuthToken { get; set; }
    public int MaxTokens { get; set; } = 4096;
    public double Temperature { get; set; } = 0.7;
    public bool IsDefault { get; set; } = false;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public List<Agent> Agents { get; set; } = new();

    public string DisplayName => $"{Name} ({ModelName})";
}
