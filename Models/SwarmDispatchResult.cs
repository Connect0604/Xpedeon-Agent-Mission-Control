namespace XpedeonAgentMissionControl.Models;

public class SwarmDispatchResult
{
    public List<string> TaskIds { get; set; } = new();
    public string FinalOutput { get; set; } = string.Empty;
    public int CompletedTaskCount { get; set; }
    public int FailedTaskCount { get; set; }
}
