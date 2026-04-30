# Encryption & Key Management Guide

This document describes how Xpedeon handles encryption of sensitive configuration values (API keys, auth tokens, connection strings).

## Overview

- **Algorithm**: DPAPI (Data Protection API) - Windows, or File-based RSA keys on Linux
- **Scope**: API keys, auth tokens, LLM provider credentials
- **Key Storage**: Platform-dependent (Windows DPAPI, Linux file-based, or Azure Key Vault)
- **Automatic**: Secrets are encrypted on save and decrypted on load
- **Audited**: Encryption events logged for compliance

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│ LLMProvider (plaintext in memory)                           │
│ - ApiKey: "sk-123..."                                       │
│ - IsApiKeyEncrypted: false                                  │
└─────────────┬───────────────────────────────────────────────┘
              │
              ▼
┌─────────────────────────────────────────────────────────────┐
│ SecretEncryptionService.EncryptProviderSecretsAsync()       │
│ - Encrypt plaintext values                                  │
│ - Set IsApiKeyEncrypted = true                              │
│ - Update EncryptionUpdatedAt                                │
└─────────────┬───────────────────────────────────────────────┘
              │
              ▼
┌─────────────────────────────────────────────────────────────┐
│ SecretManager (uses DPAPI)                                  │
│ - Encrypt string using IDataProtector                       │
│ - Return base64-encoded ciphertext                          │
└─────────────┬───────────────────────────────────────────────┘
              │
              ▼
┌─────────────────────────────────────────────────────────────┐
│ Database (encrypted at rest)                                │
│ - ApiKey: "BQA..."  (base64 ciphertext)                     │
│ - IsApiKeyEncrypted: true                                   │
└─────────────────────────────────────────────────────────────┘
```

## Configuration

### appsettings.json

```json
{
  "DataProtection": {
    "KeyStorageType": "Windows",     // Options: Windows, Linux, Azure
    "KeyStoragePath": null,          // For Linux: "/etc/xpedeon/keys/"
    "ApplicationName": "XpedeonAgentMissionControl",
    "StrictKeyRotation": false
  }
}
```

### Platform-Specific Setup

#### Windows (DPAPI - Automatic)
- Uses Windows Data Protection API
- Keys stored in Windows credential manager
- **No manual setup required**
- User running app must have access to Windows DPAPI

#### Linux (File-Based RSA Keys)
```json
{
  "DataProtection": {
    "KeyStorageType": "Linux",
    "KeyStoragePath": "/etc/xpedeon/keys/"
  }
}
```

**Setup steps:**
```bash
# Create key directory
mkdir -p /etc/xpedeon/keys/
chmod 700 /etc/xpedeon/keys/
chown app_user:app_group /etc/xpedeon/keys/

# App will auto-generate keys on first run
```

#### Azure Key Vault
```json
{
  "DataProtection": {
    "KeyStorageType": "Azure",
    "AzureKeyVaultUrl": "https://myvault.vault.azure.net/"
  }
}
```

**Setup steps:**
- Install Azure SDK NuGetpackage
- Configure managed identity or service principal
- Grant "Key Wrap, Key Unwrap" permissions on vault

## Usage

### Encrypting Secrets

```csharp
var encryptionService = serviceProvider.GetRequiredService<SecretEncryptionService>();

var provider = new LLMProvider
{
    Id = "claude-1",
    Name = "Claude",
    ApiKey = "sk-123456..."  // plaintext
};

// Automatically encrypts ApiKey and sets IsApiKeyEncrypted = true
await encryptionService.EncryptProviderSecretsAsync(provider);

// Save to database
await db.SaveChangesAsync();
```

### Decrypting Secrets

```csharp
// Get decrypted key (in-memory only)
var (decryptedApiKey, decryptedAuthToken) = 
    await encryptionService.DecryptProviderSecretsAsync(provider);

// Use decrypted value
var response = await httpClient.PostAsync(
    $"{provider.Endpoint}/chat",
    new StringContent($"Authorization: Bearer {decryptedApiKey}")
);

// Original provider.ApiKey remains encrypted in database
```

### Automatic Encryption on Startup

On app startup, any unencrypted secrets are automatically encrypted:

```csharp
var encryptedCount = await encryptionService.EncryptUnencryptedSecretsAsync();
if (encryptedCount > 0)
{
    logger.LogWarning("Auto-encrypted {Count} providers", encryptedCount);
}
```

## Key Rotation

### Manual Rotation

When encryption keys expire (policy-dependent), rotate them:

```csharp
var rotatedCount = await encryptionService.RotateAllKeysAsync();
logger.LogInformation("Rotated {Count} providers", rotatedCount);
```

### Automatic Rotation

For production, set up a scheduled task:

```csharp
// In ScheduledTaskService or Quartz job
[DisallowConcurrentExecution]
public class EncryptionKeyRotationJob : IJob
{
    private readonly SecretEncryptionService _encryptionService;

    public async Task Execute(IJobExecutionContext context)
    {
        var rotatedCount = await _encryptionService.RotateAllKeysAsync();
        // Log result
    }
}

// Configure in Program.cs
scheduler.ScheduleJob<EncryptionKeyRotationJob>("0 0 * * 0"); // Weekly
```

## Disaster Recovery

### Key Loss Scenario

If encryption keys are lost, secrets cannot be recovered. **Prevention is critical.**

#### Backup Encryption Keys

##### Linux File-Based Keys
```bash
# Backup keys (manually, daily)
tar -czf /backup/xpedeon-keys-$(date +%Y%m%d).tar.gz /etc/xpedeon/keys/

# Restore keys
tar -xzf /backup/xpedeon-keys-20260430.tar.gz -C /
chmod 700 /etc/xpedeon/keys/
```

##### Windows DPAPI
```powershell
# Export DPAPI master key (Windows Admin only)
# (This is complex; instead, use Azure Key Vault for production)

# Or backup full app state + re-enter secrets
```

##### Azure Key Vault
- Azure handles backup automatically
- Keys stored redundantly across regions
- No manual backup needed

### Secret Recovery Process

If secrets are lost and cannot be decrypted:

1. **Short-term**: Disable app and manually enter new API keys
2. **Process**:
   - Load app with restored keys (if available)
   - If keys unavailable, decrypt fails
   - Manually regenerate API keys with each provider
   - Re-enter in app via UI or API
   - Re-encrypt with new keys

3. **Prevention**:
   - Store backup of keys in separate secure location
   - Rotate keys periodically
   - Use Azure Key Vault for redundancy

### Testing Disaster Recovery

Monthly, test key restoration:

```bash
# 1. Backup current keys
cp /etc/xpedeon/keys /etc/xpedeon/keys.backup

# 2. Start fresh app instance with clean key path
# 3. Verify new keys are generated
# 4. Restore original keys
cp /etc/xpedeon/keys.backup/* /etc/xpedeon/keys/

# 5. Verify app can decrypt secrets
# 6. Log test result in incident tracking
```

## Security Best Practices

1. **Never log secrets** — Secrets logged as "***" or [REDACTED]
2. **Decrypt on-demand** — Keep decrypted values in-memory only, never persisted
3. **Minimal exposure** — Only decrypt when actually calling external API
4. **Rotate regularly** — Quarterly minimum, monthly recommended for production
5. **Access control** — Only app process can read key storage directory
6. **Audit logging** — All encryption/decryption events logged
7. **Backup separately** — Keys backed up to separate secure location
8. **Alert on rotation failure** — Immediate notification if key rotation fails

## Monitoring

### Encrypted Secrets Status

```csharp
// Check for unencrypted secrets (should be 0 in production)
var unencrypted = await db.Set<LLMProvider>()
    .Where(p => (!string.IsNullOrEmpty(p.ApiKey) && !p.IsApiKeyEncrypted) ||
               (!string.IsNullOrEmpty(p.AuthToken) && !p.IsAuthTokenEncrypted))
    .ToListAsync();

if (unencrypted.Count > 0)
    logger.LogError("Found {Count} unencrypted providers", unencrypted.Count);
```

### Key Rotation Status

```csharp
// Check if any encryption is outdated
var staleEncryption = await db.Set<LLMProvider>()
    .Where(p => (p.IsApiKeyEncrypted || p.IsAuthTokenEncrypted) &&
               p.EncryptionUpdatedAt < DateTime.UtcNow.AddDays(-30))
    .ToListAsync();

if (staleEncryption.Count > 0)
    logger.LogWarning("Found {Count} providers with stale encryption", staleEncryption.Count);
```

## Troubleshooting

### "Failed to decrypt secret"

**Symptoms**: Decryption throws exception

**Causes**:
- Encryption keys deleted or corrupted
- Running on different machine (DPAPI is machine-specific)
- Key storage path inaccessible
- Corrupted ciphertext in database

**Resolution**:
1. Check key storage directory exists and is readable
2. Verify app has file access permissions
3. Check application logs for detailed error
4. If keys lost, restore from backup
5. Last resort: manually regenerate all secrets

### "Encryption key rotation failed"

**Symptoms**: Rotation job fails, keys not rotated

**Causes**:
- Key generation error
- Database connectivity issue
- Insufficient disk space

**Resolution**:
1. Check logs for root cause
2. Verify database connectivity
3. Verify key storage has space
4. Retry rotation manually
5. Alert if persistent

## References

- [PRODUCTION_ROADMAP.md](../PRODUCTION_ROADMAP.md) - Phase 1.3 encryption requirements
- [Microsoft DPAPI Documentation](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/)
- [Key Management Best Practices](https://cheatsheetseries.owasp.org/cheatsheets/Secrets_Management_Cheat_Sheet.html)
