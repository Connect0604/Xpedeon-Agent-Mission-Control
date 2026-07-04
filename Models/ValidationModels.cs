namespace XpedeonAgentMissionControl.Models;

/// <summary>
/// Request DTOs for input validation
/// These define the contract for API/form inputs
/// </summary>

// Agent Operations
public class CreateAgentRequest
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? SystemPrompt { get; set; }
    public int? LLMProviderId { get; set; }
    public bool SpawnEnabled { get; set; }
    public int MaxSpawns { get; set; } = 5;
    public int MaxSpawnDepth { get; set; } = 3;
    public string? SpawnStrategy { get; set; }
    public string? AggregationStrategy { get; set; }
}

public class UpdateAgentRequest
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? SystemPrompt { get; set; }
    public int? LLMProviderId { get; set; }
    public bool? SpawnEnabled { get; set; }
    public int? MaxSpawns { get; set; }
    public int? MaxSpawnDepth { get; set; }
    public string? SpawnStrategy { get; set; }
    public string? AggregationStrategy { get; set; }
}

// Task Operations
public class CreateTaskRequest
{
    public required int AgentId { get; set; }
    public required string Input { get; set; }
    public string Priority { get; set; } = "Normal"; // Low, Normal, High, Urgent
    public bool RequiresApproval { get; set; }
    public int? LocalCapabilityId { get; set; }
}

public class ApproveTaskRequest
{
    public required int TaskId { get; set; }
    public string? ApprovalNote { get; set; }
}

public class CancelTaskRequest
{
    public required int TaskId { get; set; }
    public string? CancellationReason { get; set; }
}

// LLM Provider Operations
public class CreateLLMProviderRequest
{
    public required string Name { get; set; }
    public required string ProviderType { get; set; } // Claude, OpenAI, Ollama, Custom
    public required string ModelName { get; set; }
    public string? Endpoint { get; set; }
    public required string ApiKey { get; set; }
    public string? AuthToken { get; set; }
    public int MaxTokens { get; set; } = 4096;
    public double Temperature { get; set; } = 0.7;
    public decimal CostPer1kInputTokens { get; set; }
    public decimal CostPer1kOutputTokens { get; set; }
}

public class UpdateLLMProviderRequest
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? ModelName { get; set; }
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? AuthToken { get; set; }
    public int? MaxTokens { get; set; }
    public double? Temperature { get; set; }
    public decimal? CostPer1kInputTokens { get; set; }
    public decimal? CostPer1kOutputTokens { get; set; }
    public bool? IsEnabled { get; set; }
}

// Skill Operations
public class CreateSkillRequest
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string PromptSnippet { get; set; }
    public string? MCPToolAllowList { get; set; } // JSON array
    public string? Category { get; set; }
}

// External Skill Package Operations
public class ImportSkillPackageRequest
{
    public required string GitHubUrl { get; set; }
    public string? Name { get; set; }
}

public class ApproveSkillPackageRequest
{
    public required int PackageId { get; set; }
}

// Workflow Operations
public class CreateWorkflowRequest
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string DefinitionJson { get; set; } // Validated as JSON
}

public class CreateWorkflowStepRequest
{
    public required int WorkflowId { get; set; }
    public required int StepNumber { get; set; }
    public required int AgentId { get; set; }
    public string? Input { get; set; }
}

// Capability Operations
public class CreateCapabilityRequest
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string CapabilityType { get; set; }
    public required string PayloadJson { get; set; } // Validated as JSON
}

// Swarm Operations
public class CreateSwarmRequest
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string CoordinationStrategy { get; set; }
    public string? ConfigJson { get; set; }
}
