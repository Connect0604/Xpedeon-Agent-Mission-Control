using System.Text.Json.Serialization;

namespace XpedeonAgentMissionControl.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum LocalAutomationActionType
    {
        CreateDirectory,
        Copy,
        Move,
        Rename,
        Delete,
        WriteTextFile,
        Zip,
        Unzip,
        RunPowerShell
    }

    public sealed class LocalAutomationPlan
    {
        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("actions")]
        public List<LocalAutomationAction> Actions { get; set; } = new();
    }

    public sealed class LocalAutomationAction
    {
        [JsonPropertyName("type")]
        public LocalAutomationActionType Type { get; set; }

        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("destination")]
        public string? Destination { get; set; }

        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("script")]
        public string? Script { get; set; }

        [JsonPropertyName("arguments")]
        public string? Arguments { get; set; }

        [JsonPropertyName("overwrite")]
        public bool Overwrite { get; set; }

        [JsonPropertyName("createParents")]
        public bool CreateParents { get; set; }
    }

    public sealed class LocalAutomationActionResult
    {
        [JsonPropertyName("type")]
        public LocalAutomationActionType Type { get; set; }

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("path")]
        public string? Path { get; set; }
    }

    public sealed class LocalAutomationExecutionResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("requiresElevatedApproval")]
        public bool RequiresElevatedApproval { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("trace")]
        public string? Trace { get; set; }

        [JsonPropertyName("plan")]
        public LocalAutomationPlan? Plan { get; set; }

        [JsonPropertyName("touchedPaths")]
        public List<string> TouchedPaths { get; set; } = new();

        [JsonPropertyName("actionResults")]
        public List<LocalAutomationActionResult> ActionResults { get; set; } = new();

        [JsonPropertyName("promptTokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("completionTokens")]
        public int CompletionTokens { get; set; }

        [JsonPropertyName("totalTokens")]
        public int TotalTokens { get; set; }

        [JsonPropertyName("costUsd")]
        public decimal CostUSD { get; set; }

        [JsonPropertyName("modelUsed")]
        public string? ModelUsed { get; set; }

        [JsonPropertyName("confidenceScore")]
        public double? ConfidenceScore { get; set; }
    }
}
