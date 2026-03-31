using Microsoft.AspNetCore.SignalR;

namespace XpedeonAgentMissionControl.Hubs;

public class AgentHub : Hub
{
    public async Task JoinGroup(string group)
        => await Groups.AddToGroupAsync(Context.ConnectionId, group);

    public async Task LeaveGroup(string group)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
}
