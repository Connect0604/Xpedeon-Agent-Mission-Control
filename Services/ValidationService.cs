using System.Text.Json;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

/// <summary>
/// Validation service for request payloads
/// Provides fluent validation rules for agents, tasks, providers, etc.
/// </summary>
public class ValidationService
{
    private readonly ILogger<ValidationService> _logger;

    public ValidationService(ILogger<ValidationService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Validate CreateAgentRequest
    /// </summary>
    public ValidationResult ValidateCreateAgent(CreateAgentRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Name))
            errors.Add("Agent name is required");
        else if (request.Name.Length > 255)
            errors.Add("Agent name must be 255 characters or less");

        if (!string.IsNullOrEmpty(request.Description) && request.Description.Length > 2000)
            errors.Add("Agent description must be 2000 characters or less");

        if (!string.IsNullOrEmpty(request.SystemPrompt) && request.SystemPrompt.Length > 10000)
            errors.Add("System prompt must be 10000 characters or less");

        if (request.MaxSpawns < 0 || request.MaxSpawns > 100)
            errors.Add("MaxSpawns must be between 0 and 100");

        if (request.MaxSpawnDepth < 0 || request.MaxSpawnDepth > 10)
            errors.Add("MaxSpawnDepth must be between 0 and 10");

        if (!string.IsNullOrEmpty(request.SpawnStrategy) &&
            !new[] { "RoundRobin", "Sequential", "Random" }.Contains(request.SpawnStrategy))
            errors.Add("Invalid spawn strategy");

        if (!string.IsNullOrEmpty(request.AggregationStrategy) &&
            !new[] { "CollectAll", "FirstWins", "Voting", "LLMSynthesize" }.Contains(request.AggregationStrategy))
            errors.Add("Invalid aggregation strategy");

        return new ValidationResult { IsValid = errors.Count == 0, Errors = errors };
    }

    /// <summary>
    /// Validate CreateTaskRequest
    /// </summary>
    public ValidationResult ValidateCreateTask(CreateTaskRequest request)
    {
        var errors = new List<string>();

        if (request.AgentId <= 0)
            errors.Add("Valid AgentId is required");

        if (string.IsNullOrWhiteSpace(request.Input))
            errors.Add("Task input is required");
        else if (request.Input.Length > 50000)
            errors.Add("Task input must be 50000 characters or less");

        var validPriorities = new[] { "Low", "Normal", "High", "Urgent" };
        if (!validPriorities.Contains(request.Priority))
            errors.Add("Invalid task priority. Must be: Low, Normal, High, or Urgent");

        return new ValidationResult { IsValid = errors.Count == 0, Errors = errors };
    }

    /// <summary>
    /// Validate CreateLLMProviderRequest
    /// </summary>
    public ValidationResult ValidateCreateLLMProvider(CreateLLMProviderRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Name))
            errors.Add("Provider name is required");
        else if (request.Name.Length > 255)
            errors.Add("Provider name must be 255 characters or less");

        var validTypes = new[] { "Claude", "OpenAI", "Ollama", "Custom" };
        if (!validTypes.Contains(request.ProviderType))
            errors.Add("Invalid provider type. Must be: Claude, OpenAI, Ollama, or Custom");

        if (string.IsNullOrWhiteSpace(request.ModelName))
            errors.Add("Model name is required");
        else if (request.ModelName.Length > 255)
            errors.Add("Model name must be 255 characters or less");

        if (string.IsNullOrWhiteSpace(request.ApiKey))
            errors.Add("API key is required");
        else if (request.ApiKey.Length < 10 || request.ApiKey.Length > 500)
            errors.Add("API key must be between 10 and 500 characters");

        if (!string.IsNullOrEmpty(request.Endpoint) && !IsValidUrl(request.Endpoint))
            errors.Add("Endpoint must be a valid URL");

        if (request.MaxTokens < 1 || request.MaxTokens > 100000)
            errors.Add("MaxTokens must be between 1 and 100000");

        if (request.Temperature < 0 || request.Temperature > 2)
            errors.Add("Temperature must be between 0 and 2");

        if (request.CostPer1kInputTokens < 0)
            errors.Add("Input token cost cannot be negative");

        if (request.CostPer1kOutputTokens < 0)
            errors.Add("Output token cost cannot be negative");

        return new ValidationResult { IsValid = errors.Count == 0, Errors = errors };
    }

    /// <summary>
    /// Validate CreateSkillRequest
    /// </summary>
    public ValidationResult ValidateCreateSkill(CreateSkillRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Name))
            errors.Add("Skill name is required");
        else if (request.Name.Length > 255)
            errors.Add("Skill name must be 255 characters or less");

        if (string.IsNullOrWhiteSpace(request.PromptSnippet))
            errors.Add("Prompt snippet is required");
        else if (request.PromptSnippet.Length > 5000)
            errors.Add("Prompt snippet must be 5000 characters or less");

        if (!string.IsNullOrEmpty(request.MCPToolAllowList) && !IsValidJson(request.MCPToolAllowList))
            errors.Add("MCPToolAllowList must be valid JSON");

        return new ValidationResult { IsValid = errors.Count == 0, Errors = errors };
    }

    /// <summary>
    /// Validate CreateWorkflowRequest
    /// </summary>
    public ValidationResult ValidateCreateWorkflow(CreateWorkflowRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Name))
            errors.Add("Workflow name is required");
        else if (request.Name.Length > 255)
            errors.Add("Workflow name must be 255 characters or less");

        if (string.IsNullOrWhiteSpace(request.DefinitionJson))
            errors.Add("Workflow definition is required");
        else if (!IsValidJson(request.DefinitionJson))
            errors.Add("Workflow definition must be valid JSON");

        return new ValidationResult { IsValid = errors.Count == 0, Errors = errors };
    }

    /// <summary>
    /// Validate CreateCapabilityRequest
    /// </summary>
    public ValidationResult ValidateCreateCapability(CreateCapabilityRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Name))
            errors.Add("Capability name is required");
        else if (request.Name.Length > 255)
            errors.Add("Capability name must be 255 characters or less");

        if (string.IsNullOrWhiteSpace(request.CapabilityType))
            errors.Add("Capability type is required");

        if (string.IsNullOrWhiteSpace(request.PayloadJson))
            errors.Add("Capability payload is required");
        else if (!IsValidJson(request.PayloadJson))
            errors.Add("Capability payload must be valid JSON");

        return new ValidationResult { IsValid = errors.Count == 0, Errors = errors };
    }

    /// <summary>
    /// Validate ImportSkillPackageRequest
    /// </summary>
    public ValidationResult ValidateImportSkillPackage(ImportSkillPackageRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.GitHubUrl))
            errors.Add("GitHub URL is required");
        else if (!IsValidGitHubUrl(request.GitHubUrl))
            errors.Add("Invalid GitHub URL format");

        return new ValidationResult { IsValid = errors.Count == 0, Errors = errors };
    }

    // Helper Methods

    private bool IsValidUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }
        catch
        {
            return false;
        }
    }

    private bool IsValidGitHubUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.Host.Contains("github.com", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool IsValidJson(string json)
    {
        try
        {
            JsonDocument.Parse(json);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Generic validation result
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();

    public string ErrorMessage => string.Join("; ", Errors);
}
