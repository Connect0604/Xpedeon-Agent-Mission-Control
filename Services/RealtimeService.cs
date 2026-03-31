using Microsoft.AspNetCore.SignalR;
using XpedeonAgentMissionControl.Hubs;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

/// <summary>
/// Broadcasts real-time events to connected clients via SignalR.
/// Call these methods from AgentService / TaskService after state changes.
/// </summary>
public class RealtimeService
{
    private readonly IHubContext<AgentHub> _hub;

    public RealtimeService(IHubContext<AgentHub> hub) => _hub = hub;

    public Task AgentUpdatedAsync(string agentId)
        => _hub.Clients.All.SendAsync("AgentUpdated", agentId);

    public Task TaskUpdatedAsync(string taskId, string agentId)
        => _hub.Clients.All.SendAsync("TaskUpdated", taskId, agentId);

    public Task LogAddedAsync(string agentId, string message, string level)
        => _hub.Clients.All.SendAsync("LogAdded", agentId, message, level);

    public Task DashboardRefreshAsync()
        => _hub.Clients.All.SendAsync("DashboardRefresh");

    public Task AgentCreatedAsync(string agentId)
        => _hub.Clients.All.SendAsync("AgentCreated", agentId);
}
