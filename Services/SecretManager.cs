using Microsoft.AspNetCore.DataProtection;
using XpedeonAgentMissionControl.Configuration;

namespace XpedeonAgentMissionControl.Services;

/// <summary>
/// Manages encryption/decryption of sensitive configuration values
/// Uses .NET Data Protection API (DPAPI) for secure key management
/// </summary>
public class SecretManager
{
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly IDataProtector _dataProtector;
    private readonly ILogger<SecretManager> _logger;
    private readonly DataProtectionConfig _config;

    public SecretManager(
        IDataProtectionProvider dataProtectionProvider,
        DataProtectionConfig config,
        ILogger<SecretManager> logger)
    {
        _dataProtectionProvider = dataProtectionProvider;
        _config = config;
        _logger = logger;

        // Create a scoped protector with purpose string to prevent accidental cross-service decryption
        _dataProtector = _dataProtectionProvider.CreateProtector(
            "XpedeonAgentMissionControl.SecretManager",
            "v1",
            _config.ApplicationName);
    }

    /// <summary>
    /// Encrypt a sensitive value (e.g., API key, password)
    /// </summary>
    /// <param name="plaintext">The unencrypted value</param>
    /// <returns>Encrypted value as base64 string</returns>
    public string Encrypt(string plaintext)
    {
        try
        {
            if (string.IsNullOrEmpty(plaintext))
                return string.Empty;

            var encryptedBytes = _dataProtector.Protect(System.Text.Encoding.UTF8.GetBytes(plaintext));
            var base64 = Convert.ToBase64String(encryptedBytes);

            _logger.LogDebug("Secret encrypted successfully");
            return base64;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encrypt secret");
            throw;
        }
    }

    /// <summary>
    /// Decrypt a sensitive value
    /// </summary>
    /// <param name="ciphertext">The encrypted value (base64)</param>
    /// <returns>Decrypted plaintext value</returns>
    public string Decrypt(string ciphertext)
    {
        try
        {
            if (string.IsNullOrEmpty(ciphertext))
                return string.Empty;

            var encryptedBytes = Convert.FromBase64String(ciphertext);
            var decryptedBytes = _dataProtector.Unprotect(encryptedBytes);
            var plaintext = System.Text.Encoding.UTF8.GetString(decryptedBytes);

            _logger.LogDebug("Secret decrypted successfully");
            return plaintext;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt secret. Keys may have been rotated or corrupted.");
            throw;
        }
    }

    /// <summary>
    /// Check if a value appears to be encrypted (base64)
    /// </summary>
    public static bool IsEncrypted(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        try
        {
            // Try to parse as base64
            Convert.FromBase64String(value);
            // Also check for minimum length (encrypted values are typically longer)
            return value.Length > 50;
        }
        catch
        {
            return false;
        }
    }
}
