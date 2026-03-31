namespace XpedeonAgentMissionControl.Models;

public enum MemoryType { ShortTerm, LongTerm, Swarm, Episodic }

public class AgentMemory
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string AgentId { get; set; } = string.Empty;
    public string? SwarmId { get; set; }
    public MemoryType Type { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? TaskId { get; set; }
    public double? RelevanceScore { get; set; }
    public int AccessCount { get; set; } = 0;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }

    // Navigation
    public Agent? Agent { get; set; }
}
