using System.Text.Json;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public sealed class LocalAutomationResponseFormatter
{
    public string FormatSuccess(LocalOrchestrationExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.LocalCapabilityId is not null)
        {
            var capabilityMessage = TryFormatCapabilitySuccess(result);
            if (!string.IsNullOrWhiteSpace(capabilityMessage))
            {
                return capabilityMessage;
            }
        }

        var actionMessages = result.ActionResults
            .Select(FormatActionResult)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .ToList();

        if (actionMessages.Count > 0)
        {
            return string.Join(Environment.NewLine, actionMessages);
        }

        return string.IsNullOrWhiteSpace(result.Message)
            ? "Local automation completed successfully."
            : result.Message;
    }

    public string FormatFailure(LocalOrchestrationExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var message = result.Message ?? result.Output ?? string.Empty;
        if (message.Contains("outside an allowed root", StringComparison.OrdinalIgnoreCase))
        {
            return "Couldn't complete the local automation because the path is outside this agent's allowed roots.";
        }

        if (message.Contains("unknown local capability", StringComparison.OrdinalIgnoreCase))
        {
            return "Couldn't complete the local automation because the planner returned an unsupported capability. Please try rephrasing the request.";
        }

        if (message.Contains("PowerShell", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("allow", StringComparison.OrdinalIgnoreCase))
        {
            return "Couldn't run the requested script because this agent is not allowed to execute PowerShell.";
        }

        return string.IsNullOrWhiteSpace(message)
            ? "Couldn't complete the local automation."
            : $"Couldn't complete the local automation: {message}";
    }

    private static string? TryFormatCapabilitySuccess(LocalOrchestrationExecutionResult result)
    {
        var rawCapabilityOutput = string.IsNullOrWhiteSpace(result.LocalCapabilityExecutionJson)
            ? result.Output
            : result.LocalCapabilityExecutionJson;

        if (string.IsNullOrWhiteSpace(rawCapabilityOutput))
        {
            return NormalizeMessage(result.Message);
        }

        try
        {
            using var document = JsonDocument.Parse(rawCapabilityOutput);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Number)
            {
                if (root.TryGetInt64(out var intCount))
                {
                    return $"Count result: {intCount}";
                }

                if (root.TryGetDouble(out var doubleCount))
                {
                    return $"Count result: {doubleCount}";
                }
            }

            if (root.ValueKind == JsonValueKind.String)
            {
                var scalarText = root.GetString();
                if (!string.IsNullOrWhiteSpace(scalarText))
                {
                    return scalarText;
                }
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return NormalizeMessage(result.Message);
            }

            if (root.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String)
            {
                var message = messageElement.GetString();
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }
            }

            if (root.TryGetProperty("output", out var outputElement) && outputElement.ValueKind == JsonValueKind.String)
            {
                var outputText = outputElement.GetString();
                if (!string.IsNullOrWhiteSpace(outputText))
                {
                    return outputText;
                }
            }

            if (root.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String)
            {
                var contentText = contentElement.GetString();
                if (!string.IsNullOrWhiteSpace(contentText))
                {
                    return contentText;
                }
            }

            if (root.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String)
            {
                var path = pathElement.GetString();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    if (result.LocalCapabilityId?.Contains("createDirectory", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return $"Created folder: {path}";
                    }

                    if (result.LocalCapabilityId?.Contains("writeTextFile", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return $"Created file: {path}";
                    }

                    return $"Completed local action for: {path}";
                }
            }

            if (root.TryGetProperty("destination", out var destinationElement) && destinationElement.ValueKind == JsonValueKind.String)
            {
                var destination = destinationElement.GetString();
                if (!string.IsNullOrWhiteSpace(destination))
                {
                    return $"Completed local action for: {destination}";
                }
            }

            if (root.TryGetProperty("count", out var countElement) && countElement.TryGetInt32(out var count))
            {
                return $"Count result: {count}";
            }

            // Keep object output visible instead of falling back to generic capability status text.
            return root.GetRawText();
        }
        catch (JsonException)
        {
            // Non-JSON output (common for PowerShell capability scripts).
            if (!string.IsNullOrWhiteSpace(rawCapabilityOutput))
            {
                return rawCapabilityOutput;
            }
        }

        return NormalizeMessage(result.Message);
    }

    private static string? FormatActionResult(LocalAutomationActionResult actionResult)
    {
        if (!string.IsNullOrWhiteSpace(actionResult.Path))
        {
            return actionResult.Type switch
            {
                LocalAutomationActionType.CreateDirectory => $"Created folder: {actionResult.Path}",
                LocalAutomationActionType.WriteTextFile => $"Created file: {actionResult.Path}",
                LocalAutomationActionType.Copy => $"Copied item to: {actionResult.Path}",
                LocalAutomationActionType.Move => $"Moved item to: {actionResult.Path}",
                LocalAutomationActionType.Rename => $"Renamed item to: {actionResult.Path}",
                LocalAutomationActionType.Delete => $"Deleted: {actionResult.Path}",
                LocalAutomationActionType.Zip => $"Created zip: {actionResult.Path}",
                LocalAutomationActionType.Unzip => $"Extracted to: {actionResult.Path}",
                _ => NormalizeMessage(actionResult.Message)
            };
        }

        return NormalizeMessage(actionResult.Message);
    }

    private static string? NormalizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var normalized = message
            .Replace("Created directory", "Created folder", StringComparison.OrdinalIgnoreCase)
            .Replace("Wrote text file", "Created file", StringComparison.OrdinalIgnoreCase);

        if (normalized.StartsWith("Created folder '", StringComparison.OrdinalIgnoreCase) &&
            normalized.EndsWith("'.", StringComparison.Ordinal))
        {
            var path = normalized["Created folder '".Length..^2];
            return $"Created folder: {path}";
        }

        if (normalized.StartsWith("Created file '", StringComparison.OrdinalIgnoreCase) &&
            normalized.EndsWith("'.", StringComparison.Ordinal))
        {
            var path = normalized["Created file '".Length..^2];
            return $"Created file: {path}";
        }

        return normalized;
    }
}
