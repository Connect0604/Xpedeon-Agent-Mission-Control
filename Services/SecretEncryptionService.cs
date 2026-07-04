using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

/// <summary>
/// High-level service for managing LLM provider secret encryption
/// Handles automatic encrypt/decrypt on save/load, auditing, and key rotation
/// </summary>
public class SecretEncryptionService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly SecretManager _secretManager;
    private readonly ILogger<SecretEncryptionService> _logger;

    public SecretEncryptionService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        SecretManager secretManager,
        ILogger<SecretEncryptionService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _secretManager = secretManager;
        _logger = logger;
    }

    /// <summary>
    /// Encrypt and save provider secrets to database
    /// </summary>
    public async Task EncryptProviderSecretsAsync(LLMProvider provider)
    {
        try
        {
            if (!string.IsNullOrEmpty(provider.ApiKey) && !provider.IsApiKeyEncrypted)
            {
                provider.ApiKey = _secretManager.Encrypt(provider.ApiKey);
                provider.IsApiKeyEncrypted = true;
                _logger.LogInformation("API key encrypted for provider {ProviderId}", provider.Id);
            }

            if (!string.IsNullOrEmpty(provider.AuthToken) && !provider.IsAuthTokenEncrypted)
            {
                provider.AuthToken = _secretManager.Encrypt(provider.AuthToken);
                provider.IsAuthTokenEncrypted = true;
                _logger.LogInformation("Auth token encrypted for provider {ProviderId}", provider.Id);
            }

            provider.EncryptionUpdatedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encrypt secrets for provider {ProviderId}", provider.Id);
            throw;
        }
    }

    /// <summary>
    /// Decrypt provider secrets from database (in-memory only, doesn't save)
    /// </summary>
    public async Task<(string? ApiKey, string? AuthToken)> DecryptProviderSecretsAsync(LLMProvider provider)
    {
        try
        {
            var apiKey = provider.ApiKey;
            var authToken = provider.AuthToken;

            if (!string.IsNullOrEmpty(apiKey) && provider.IsApiKeyEncrypted)
            {
                apiKey = _secretManager.Decrypt(apiKey);
                _logger.LogDebug("API key decrypted for provider {ProviderId}", provider.Id);
            }

            if (!string.IsNullOrEmpty(authToken) && provider.IsAuthTokenEncrypted)
            {
                authToken = _secretManager.Decrypt(authToken);
                _logger.LogDebug("Auth token decrypted for provider {ProviderId}", provider.Id);
            }

            return (apiKey, authToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt secrets for provider {ProviderId}", provider.Id);
            throw;
        }
    }

    /// <summary>
    /// Get decrypted API key for a provider
    /// </summary>
    public async Task<string?> GetDecryptedApiKeyAsync(string providerId)
    {
        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var provider = await db.Set<LLMProvider>().FirstOrDefaultAsync(p => p.Id == providerId);
            if (provider == null)
                return null;

            var (apiKey, _) = await DecryptProviderSecretsAsync(provider);
            return apiKey;
        }
    }

    /// <summary>
    /// Get decrypted auth token for a provider
    /// </summary>
    public async Task<string?> GetDecryptedAuthTokenAsync(string providerId)
    {
        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var provider = await db.Set<LLMProvider>().FirstOrDefaultAsync(p => p.Id == providerId);
            if (provider == null)
                return null;

            var (_, authToken) = await DecryptProviderSecretsAsync(provider);
            return authToken;
        }
    }

    /// <summary>
    /// Rotate encryption keys for all providers (call when keys expire)
    /// </summary>
    public async Task<int> RotateAllKeysAsync()
    {
        var rotatedCount = 0;
        try
        {
            using (var db = await _dbContextFactory.CreateDbContextAsync())
            {
                var providers = await db.Set<LLMProvider>()
                    .Where(p => p.IsApiKeyEncrypted || p.IsAuthTokenEncrypted)
                    .ToListAsync();

                foreach (var provider in providers)
                {
                    var (decryptedApiKey, decryptedAuthToken) = await DecryptProviderSecretsAsync(provider);

                    // Re-encrypt with current key
                    provider.ApiKey = !string.IsNullOrEmpty(decryptedApiKey) ? _secretManager.Encrypt(decryptedApiKey) : null;
                    provider.AuthToken = !string.IsNullOrEmpty(decryptedAuthToken) ? _secretManager.Encrypt(decryptedAuthToken) : null;
                    provider.EncryptionUpdatedAt = DateTime.UtcNow;

                    rotatedCount++;
                }

                await db.SaveChangesAsync();
                _logger.LogInformation("Rotated encryption keys for {Count} providers", rotatedCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rotate encryption keys");
            throw;
        }

        return rotatedCount;
    }

    /// <summary>
    /// Check for unencrypted secrets and encrypt them
    /// Call this on startup to ensure all secrets are encrypted
    /// </summary>
    public async Task<int> EncryptUnencryptedSecretsAsync()
    {
        var encryptedCount = 0;
        try
        {
            using (var db = await _dbContextFactory.CreateDbContextAsync())
            {
                var providersWithUnencrypted = await db.Set<LLMProvider>()
                    .Where(p => (!string.IsNullOrEmpty(p.ApiKey) && !p.IsApiKeyEncrypted) ||
                               (!string.IsNullOrEmpty(p.AuthToken) && !p.IsAuthTokenEncrypted))
                    .ToListAsync();

                foreach (var provider in providersWithUnencrypted)
                {
                    await EncryptProviderSecretsAsync(provider);
                    encryptedCount++;
                }

                if (encryptedCount > 0)
                {
                    await db.SaveChangesAsync();
                    _logger.LogWarning("Encrypted {Count} providers with unencrypted secrets on startup. " +
                                     "Ensure this doesn't happen in production.", encryptedCount);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encrypt unencrypted secrets on startup");
            throw;
        }

        return encryptedCount;
    }
}
