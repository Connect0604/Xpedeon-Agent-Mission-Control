using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

/// <summary>
/// Service for managing API keys: generation, validation, revocation, tracking
/// </summary>
public class ApiKeyService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger<ApiKeyService> _logger;

    public ApiKeyService(IDbContextFactory<AppDbContext> dbContextFactory, ILogger<ApiKeyService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Generate a new API key (returns plaintext, only shown once)
    /// </summary>
    public async Task<(string Key, ApiKey Model)> GenerateKeyAsync(
        string name,
        string description = "",
        string permissions = "default",
        DateTime? expiresAt = null,
        string? createdByUser = null)
    {
        // Generate random key (32 bytes = 256 bits = 64 hex chars)
        var keyBytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(keyBytes);
        }
        var keyString = Convert.ToHexString(keyBytes).ToLower();

        // Hash the key for storage
        var hashedValue = HashApiKey(keyString);
        var lastFour = keyString.Substring(keyString.Length - 4);

        var apiKey = new ApiKey
        {
            Name = name,
            Description = description,
            HashedValue = hashedValue,
            LastFourCharacters = lastFour,
            Permissions = permissions,
            ExpiresAt = expiresAt,
            CreatedAt = DateTime.UtcNow,
            CreatedByUser = createdByUser
        };

        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            db.Set<ApiKey>().Add(apiKey);
            await db.SaveChangesAsync();
        }

        _logger.LogInformation("API Key '{Name}' created by {User}", name, createdByUser ?? "system");
        return (keyString, apiKey);
    }

    /// <summary>
    /// Validate an API key and mark as used
    /// </summary>
    public async Task<ApiKey?> ValidateAndTrackAsync(string key)
    {
        var hashedValue = HashApiKey(key);

        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var apiKey = await db.Set<ApiKey>()
                .FirstOrDefaultAsync(k => k.HashedValue == hashedValue);

            if (apiKey == null)
            {
                _logger.LogWarning("API key validation failed: key not found or invalid");
                return null;
            }

            if (!apiKey.IsValid)
            {
                var reason = apiKey.RevokedAt.HasValue ? "revoked" : "expired";
                _logger.LogWarning("API key validation failed: key is {Reason}", reason);
                return null;
            }

            // Update usage tracking
            apiKey.LastUsedAt = DateTime.UtcNow;
            apiKey.UsageCount++;
            await db.SaveChangesAsync();

            return apiKey;
        }
    }

    /// <summary>
    /// Get an API key by ID
    /// </summary>
    public async Task<ApiKey?> GetKeyAsync(int id)
    {
        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            return await db.Set<ApiKey>().FirstOrDefaultAsync(k => k.Id == id);
        }
    }

    /// <summary>
    /// List all API keys (non-sensitive data)
    /// </summary>
    public async Task<List<ApiKeyResponse>> ListKeysAsync(bool includeRevoked = false)
    {
        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var query = db.Set<ApiKey>().AsQueryable();

            if (!includeRevoked)
                query = query.Where(k => k.RevokedAt == null);

            return await query
                .Select(k => new ApiKeyResponse
                {
                    Id = k.Id,
                    Name = k.Name,
                    Description = k.Description,
                    LastFourCharacters = k.LastFourCharacters,
                    Permissions = k.Permissions,
                    IsValid = k.IsValid,
                    IsExpired = k.IsExpired,
                    CreatedAt = k.CreatedAt,
                    ExpiresAt = k.ExpiresAt,
                    RevokedAt = k.RevokedAt,
                    LastUsedAt = k.LastUsedAt,
                    UsageCount = k.UsageCount
                })
                .ToListAsync();
        }
    }

    /// <summary>
    /// Revoke an API key
    /// </summary>
    public async Task<bool> RevokeKeyAsync(int id, string? revokedByUser = null)
    {
        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var apiKey = await db.Set<ApiKey>().FirstOrDefaultAsync(k => k.Id == id);
            if (apiKey == null)
                return false;

            if (apiKey.RevokedAt.HasValue)
                return true; // Already revoked

            apiKey.RevokedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            _logger.LogInformation("API Key '{Name}' revoked by {User}", apiKey.Name, revokedByUser ?? "system");
            return true;
        }
    }

    /// <summary>
    /// Delete an API key
    /// </summary>
    public async Task<bool> DeleteKeyAsync(int id)
    {
        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var apiKey = await db.Set<ApiKey>().FirstOrDefaultAsync(k => k.Id == id);
            if (apiKey == null)
                return false;

            db.Set<ApiKey>().Remove(apiKey);
            await db.SaveChangesAsync();

            _logger.LogInformation("API Key '{Name}' deleted", apiKey.Name);
            return true;
        }
    }

    /// <summary>
    /// Hash an API key using SHA-256
    /// </summary>
    private static string HashApiKey(string key)
    {
        using (var sha256 = SHA256.Create())
        {
            var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(key));
            return Convert.ToHexString(hashedBytes).ToLower();
        }
    }

    /// <summary>
    /// Clean up expired keys (for maintenance)
    /// </summary>
    public async Task<int> DeleteExpiredKeysAsync()
    {
        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var expiredKeys = await db.Set<ApiKey>()
                .Where(k => k.ExpiresAt != null && k.ExpiresAt <= DateTime.UtcNow)
                .ToListAsync();

            if (expiredKeys.Count == 0)
                return 0;

            db.Set<ApiKey>().RemoveRange(expiredKeys);
            await db.SaveChangesAsync();

            _logger.LogInformation("Deleted {Count} expired API keys", expiredKeys.Count);
            return expiredKeys.Count;
        }
    }
}
