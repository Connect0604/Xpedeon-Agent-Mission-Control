namespace XpedeonAgentMissionControl.Models;

public enum ToolType { HttpRequest, DatabaseQuery, FileIO, Email, Script, WebSearch, Calculator, Custom }

public class AgentTool
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string AgentId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ToolType Type { get; set; }
    public string Config { get; set; } = "{}";  // JSON config
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Agent? Agent { get; set; }
}
