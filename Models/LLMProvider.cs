namespace XpedeonAgentMissionControl.Models;

public enum LLMProviderType { Claude, OpenAI, Ollama, Custom }

public class LLMProvider
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public LLMProviderType Type { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public string? Endpoint { get; set; }

    // Sensitive fields - stored encrypted in database
    public string? ApiKey { get; set; }
    public string? AuthToken { get; set; }

    // Encryption metadata
    public bool IsApiKeyEncrypted { get; set; } = false;
    public bool IsAuthTokenEncrypted { get; set; } = false;
    public DateTime? EncryptionUpdatedAt { get; set; }

    // Non-sensitive fields
    public int MaxTokens { get; set; } = 4096;
    public double Temperature { get; set; } = 0.7;
    public decimal CostPer1kInputTokens { get; set; } = 0m;
    public decimal CostPer1kOutputTokens { get; set; } = 0m;
    public bool IsDefault { get; set; } = false;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public List<Agent> Agents { get; set; } = new();

    public string DisplayName => $"{Name} ({ModelName})";

    /// <summary>
    /// Checks if sensitive fields need encryption
    /// </summary>
    public bool HasUnencryptedSecrets =>
        (!string.IsNullOrEmpty(ApiKey) && !IsApiKeyEncrypted) ||
        (!string.IsNullOrEmpty(AuthToken) && !IsAuthTokenEncrypted);
}

