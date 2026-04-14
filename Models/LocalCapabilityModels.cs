using System.Text.Json.Serialization;

namespace XpedeonAgentMissionControl.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LocalCapabilityCategory
{
    Read,
    Write,
    Query,
    Destructive
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LocalCapabilityExecutionType
{
    BuiltIn,
    PowerShell
}

public sealed class LocalCapabilityInvocation
{
    public string Summary { get; set; } = string.Empty;
    public string Mode { get; set; } = "invokeCapability";
    public string? CapabilityName { get; set; }
    public Dictionary<string, string?> Inputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public LocalCapabilityDraft? Draft { get; set; }
}

public sealed class LocalCapabilityDraft
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string ExecutionType { get; set; } = "PowerShell";
    public Dictionary<string, string> Inputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Script { get; set; } = string.Empty;
}

public sealed class LocalCapabilityExecutionResult
{
    public bool Success { get; set; }
    public bool RequiresElevatedApproval { get; set; }
    public string? Message { get; set; }
    public string? Trace { get; set; }
    public string? Output { get; set; }
}
