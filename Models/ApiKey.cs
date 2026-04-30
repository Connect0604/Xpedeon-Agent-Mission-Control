namespace XpedeonAgentMissionControl.Models;

/// <summary>
/// Represents an API key used for authentication.
/// API keys are hashed and stored securely. Only the hash is stored in the database.
/// </summary>
public class ApiKey
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    // The hashed key value (never store plaintext)
    public string HashedValue { get; set; } = string.Empty;

    // Last 4 characters for identification (plaintext)
    public string LastFourCharacters { get; set; } = string.Empty;

    // Permission/scope
    public string Permissions { get; set; } = "default"; // Comma-separated scopes

    // Expiration
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }

    // Tracking
    public DateTime? LastUsedAt { get; set; }
    public int UsageCount { get; set; }
    public string? CreatedByUser { get; set; }

    public bool IsValid => RevokedAt == null && (ExpiresAt == null || ExpiresAt > DateTime.UtcNow);
    public bool IsExpired => ExpiresAt != null && ExpiresAt <= DateTime.UtcNow;
}
