namespace XpedeonAgentMissionControl.Models;

public enum MCPTransportType { SSE, Stdio, WebSocket, Http }

public class MCPServer
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public MCPTransportType TransportType { get; set; } = MCPTransportType.SSE;
    public string? AuthToken { get; set; }
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
