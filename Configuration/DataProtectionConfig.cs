namespace XpedeonAgentMissionControl.Configuration;

/// <summary>
/// Configuration for Data Protection API (DPAPI)
/// Handles encryption key management and storage location
/// </summary>
public class DataProtectionConfig
{
    /// <summary>
    /// Key storage location:
    /// - "Windows" = DPAPI (Windows only)
    /// - "Linux" = File-based RSA keys
    /// - "Azure" = Azure Key Vault
    /// </summary>
    public string KeyStorageType { get; set; } = "Windows";

    /// <summary>
    /// Path where encryption keys are stored (for file-based storage)
    /// Default: /etc/xpedeon/keys/ (Linux) or %APPDATA%\xpedeon\keys\ (Windows)
    /// </summary>
    public string? KeyStoragePath { get; set; }

    /// <summary>
    /// Azure Key Vault URL (if using Azure)
    /// </summary>
    public string? AzureKeyVaultUrl { get; set; }

    /// <summary>
    /// Application name used as DPAPI discriminator
    /// Prevents keys from being decrypted by other apps
    /// </summary>
    public string ApplicationName { get; set; } = "XpedeonAgentMissionControl";

    /// <summary>
    /// Whether to throw on key rotation (vs logging warning)
    /// </summary>
    public bool StrictKeyRotation { get; set; } = false;
}
