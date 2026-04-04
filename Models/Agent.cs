namespace XpedeonAgentMissionControl.Models;

public enum AgentStatus { Active, Idle, Warning, Error, Offline }

public enum AgentType { DataSync, Reporting, Integration, Notification, Processing, Custom }

public enum TriggerType { Manual, Scheduled, Event, Swarm }

public enum ExecutionBackend { Local, HermesOpenClaw }

public class Agent
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public AgentType Type { get; set; }
    public AgentStatus Status { get; set; }
    public string Description { get; set; } = string.Empty;

    // LLM Configuration
    public string? LLMProviderId { get; set; }
    public LLMProvider? LLMProvider { get; set; }
    public string SystemPrompt { get; set; } = string.Empty;
    public ExecutionBackend ExecutionBackend { get; set; } = ExecutionBackend.Local;

    // Swarm
    public string? SwarmId { get; set; }
    public Swarm? Swarm { get; set; }

    // Template
    public bool IsTemplate { get; set; } = false;
    public string? TemplateId { get; set; }

    // Runtime info
    public string CurrentTask { get; set; } = "Idle";
    public string Version { get; set; } = "1.0.0";
    public string HostMachine { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime StartedAt { get; set; }
    public DateTime LastSeen { get; set; }

    // Metrics
    public int TasksCompleted { get; set; }
    public int TasksFailed { get; set; }
    public double CpuUsage { get; set; }
    public double MemoryUsageMB { get; set; }
    public double MemoryLimitMB { get; set; } = 512;
    public List<double> CpuHistory { get; set; } = new();

    // Memory config
    public bool ShortTermMemoryEnabled { get; set; } = true;
    public bool LongTermMemoryEnabled { get; set; } = false;
    public bool ShareMemoryWithSwarm { get; set; } = false;

    // Cost & token tracking
    public long TotalTokensUsed { get; set; }
    public decimal TotalCostUSD { get; set; }
    public long? TokenBudgetPerDay { get; set; }

    // Reliability
    public int MaxRetries { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 120;
    public bool CircuitBreakerEnabled { get; set; } = true;
    public int CircuitBreakerFailureThreshold { get; set; } = 5;
    public bool IsCircuitOpen { get; set; } = false;

    // Human-in-the-loop
    public bool RequiresApproval { get; set; } = false;
    public double ConfidenceThreshold { get; set; } = 0.8;

    // Dynamic Spawning — configuration (set by user)
    public bool SpawnEnabled { get; set; } = false;
    public AgentSpawnMode SpawnMode { get; set; } = AgentSpawnMode.RuleBased;
    public AgentSpawnTriggerType SpawnTriggerType { get; set; } = AgentSpawnTriggerType.SplitByNewline;
    public string SpawnTrigger { get; set; } = string.Empty;
    public int MaxSpawns { get; set; } = 10;
    public int MaxDepth { get; set; } = 2;
    public string ChildPromptTemplate { get; set; } = string.Empty;
    public AgentSpawnLifecycle SpawnLifecycle { get; set; } = AgentSpawnLifecycle.Ephemeral;
    public AgentSpawnAggregation SpawnAggregation { get; set; } = AgentSpawnAggregation.CollectAll;

    // Dynamic Spawning — runtime tracking (set by system)
    public bool IsEphemeral { get; set; } = false;
    public string? ParentAgentId { get; set; }
    public int SpawnDepth { get; set; } = 0;

    // Navigation
    public List<AgentTool> Tools { get; set; } = new();
    public List<AgentSkillAssignment> Skills { get; set; } = new();
    public List<AgentMCPServer> MCPServers { get; set; } = new();
    public List<AgentTask> Tasks { get; set; } = new();
    public List<AgentMemory> Memories { get; set; } = new();
    public List<PromptHistory> PromptHistory { get; set; } = new();
    public AgentSchedule? Schedule { get; set; }
    public Agent? ParentAgent { get; set; }
    public List<Agent> ChildAgents { get; set; } = new();

    // Computed
    public TimeSpan Uptime => DateTime.UtcNow - StartedAt;
    public double SuccessRate => (TasksCompleted + TasksFailed) > 0
        ? (double)TasksCompleted / (TasksCompleted + TasksFailed) * 100
        : 100;

    public string UptimeFormatted
    {
        get
        {
            var up = Uptime;
            if (up.TotalDays >= 1) return $"{(int)up.TotalDays}d {up.Hours}h";
            if (up.TotalHours >= 1) return $"{(int)up.TotalHours}h {up.Minutes}m";
            return $"{up.Minutes}m";
        }
    }
}
