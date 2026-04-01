namespace XpedeonAgentMissionControl.Models;

public enum AgentSpawnMode { RuleBased, LLMDecided }

public enum AgentSpawnTriggerType { SplitByNewline, SplitByComma, JsonArray }

public enum AgentSpawnLifecycle { Ephemeral, Pooled, Persistent }

public enum AgentSpawnAggregation { CollectAll, FirstWins, Voting, LLMSynthesize }
