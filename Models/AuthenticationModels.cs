namespace XpedeonAgentMissionControl.Models;

/// <summary>
/// Request/response DTOs for authentication
/// </summary>

public class ApiKeyCreateRequest
{
    public required string Name { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Permissions { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

public class ApiKeyCreateResponse
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Key { get; set; } // Only returned once on creation
    public string LastFourCharacters { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

public class ApiKeyResponse
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = string.Empty;
    public string LastFourCharacters { get; set; } = string.Empty;
    public string Permissions { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public bool IsExpired { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public int UsageCount { get; set; }
}

public class JwtTokenResponse
{
    public required string AccessToken { get; set; }
    public required string TokenType { get; set; } = "Bearer";
    public int ExpiresIn { get; set; } // seconds
    public DateTime IssuedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class AuthenticationErrorResponse
{
    public required string Error { get; set; }
    public string? Description { get; set; }
    public int StatusCode { get; set; }
}
