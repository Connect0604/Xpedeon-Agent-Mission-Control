namespace XpedeonAgentMissionControl.Models;

public enum SwarmStrategy { Parallel, Sequential, Voting, Pipeline, OrchestratorWorker }

public class Swarm
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string BasePrompt { get; set; } = string.Empty;
    public SwarmStrategy Strategy { get; set; } = SwarmStrategy.Parallel;
    public string? LLMProviderId { get; set; }
    public int AgentCount { get; set; }
    public string NamePattern { get; set; } = "{name} #{n}";
    public bool ShareMemory { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public List<Agent> Agents { get; set; } = new();
}
