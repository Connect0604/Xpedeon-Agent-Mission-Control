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
    public event Action? RefreshRequested;
    public event Action<TaskCompletedNotification>? TaskCompleted;

    public void NotifyTaskCompleted(TaskCompletedNotification notification)
        => TaskCompleted?.Invoke(notification);

    public RealtimeService(IHubContext<AgentHub> hub) => _hub = hub;

    public async Task AgentUpdatedAsync(string agentId)
    {
        RefreshRequested?.Invoke();
        await _hub.Clients.All.SendAsync("AgentUpdated", agentId);
    }

    public async Task TaskUpdatedAsync(string taskId, string agentId)
    {
        RefreshRequested?.Invoke();
        await _hub.Clients.All.SendAsync("TaskUpdated", taskId, agentId);
    }

    public async Task LogAddedAsync(string agentId, string message, string level)
    {
        RefreshRequested?.Invoke();
        await _hub.Clients.All.SendAsync("LogAdded", agentId, message, level);
    }

    public async Task DashboardRefreshAsync()
    {
        RefreshRequested?.Invoke();
        await _hub.Clients.All.SendAsync("DashboardRefresh");
    }

    public async Task AgentCreatedAsync(string agentId)
    {
        RefreshRequested?.Invoke();
        await _hub.Clients.All.SendAsync("AgentCreated", agentId);
    }
}

public sealed record TaskCompletedNotification(
    string TaskId,
    string AgentId,
    string AgentName,
    string TaskName,
    AgentTaskStatus Status);
