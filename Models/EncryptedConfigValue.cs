namespace XpedeonAgentMissionControl.Models;

/// <summary>
/// Represents an encrypted configuration value stored in the database
/// Tracks encryption metadata for rotation and auditing
/// </summary>
public class EncryptedConfigValue
{
    public int Id { get; set; }

    /// <summary>
    /// The key name (e.g., "LLMProvider.ApiKey", "Database.Password")
    /// </summary>
    public string KeyName { get; set; } = string.Empty;

    /// <summary>
    /// The encrypted value (base64 encoded)
    /// </summary>
    public string EncryptedValue { get; set; } = string.Empty;

    /// <summary>
    /// Data Protection Key version used for encryption
    /// Used to detect when key rotation has occurred
    /// </summary>
    public int KeyVersion { get; set; } = 1;

    /// <summary>
    /// When the value was encrypted
    /// </summary>
    public DateTime EncryptedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the encryption key will expire (triggers rotation)
    /// </summary>
    public DateTime? EncryptionKeyExpiresAt { get; set; }

    /// <summary>
    /// Whether this value needs re-encryption due to key rotation
    /// </summary>
    public bool RequiresKeyRotation { get; set; }

    /// <summary>
    /// Optional related entity (e.g., LLMProviderId) for audit tracking
    /// </summary>
    public string? RelatedEntityId { get; set; }
}
