using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public interface IMcpConnectionProbe
{
    Task<MCPConnectionTestResult> TestConnectionAsync(MCPServer server, CancellationToken cancellationToken = default);
    Task<MCPToolDiscoveryResult> ListToolsAsync(MCPServer server, CancellationToken cancellationToken = default);
}
