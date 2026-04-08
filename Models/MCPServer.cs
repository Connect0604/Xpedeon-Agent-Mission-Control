using System.ComponentModel.DataAnnotations.Schema;

namespace XpedeonAgentMissionControl.Models;

public enum MCPTransportType { SSE, Stdio, WebSocket, Http }
public enum MCPAuthMode { None, BearerToken, MicrosoftDeviceCode }

public class MCPServer
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public MCPTransportType TransportType { get; set; } = MCPTransportType.SSE;
    [NotMapped]
    public MCPAuthMode AuthMode { get; set; } = MCPAuthMode.None;
    public string? AuthToken { get; set; }
    [NotMapped]
    public string? OAuthClientId { get; set; }
    [NotMapped]
    public string? OAuthAuthority { get; set; }
    [NotMapped]
    public string? OAuthScope { get; set; }
    [NotMapped]
    public DateTime? AuthTokenExpiresAtUtc { get; set; }
    public bool IsGlobal { get; set; } = false;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public List<AgentMCPServer> AgentMCPServers { get; set; } = new();
}

public class AgentMCPServer
{
    public string AgentId { get; set; } = string.Empty;
    public string MCPServerId { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Agent? Agent { get; set; }
    public MCPServer? MCPServer { get; set; }
}
