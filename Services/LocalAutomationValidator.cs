using System.Text.Json;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public sealed class LocalAutomationValidator
{
    public LocalAutomationPlan Validate(Agent agent, LocalAutomationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(plan);

        if (!agent.LocalAutomationEnabled)
        {
            throw new InvalidOperationException("Local automation is not enabled for this agent.");
        }

        var allowedRoots = ParseAllowedRoots(agent.AllowedLocalRootsJson);

        foreach (var action in plan.Actions ?? new List<LocalAutomationAction>())
        {
            ResolveActionPaths(action, allowedRoots);
            ValidateRequiredArguments(action);
            ValidatePermissions(agent, action);
            foreach (var candidatePath in GetCandidatePaths(action))
            {
                EnsureAllowed(candidatePath, allowedRoots);
            }
        }

        return plan;
    }

    public void ValidateCapabilityInputPaths(string? allowedRootsJson, IEnumerable<string> candidatePaths)
    {
        ArgumentNullException.ThrowIfNull(candidatePaths);

        var allowedRoots = ParseAllowedRoots(allowedRootsJson);
        foreach (var candidatePath in candidatePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            EnsureAllowed(candidatePath, allowedRoots);
        }
    }

    public string ResolvePathAgainstAllowedRoots(string? allowedRootsJson, string candidatePath)
    {
        var allowedRoots = ParseAllowedRoots(allowedRootsJson);
        return ResolvePathAgainstAllowedRoots(candidatePath, allowedRoots);
    }

    private static IReadOnlyList<string> ParseAllowedRoots(string? allowedRootsJson)
    {
        if (string.IsNullOrWhiteSpace(allowedRootsJson))
        {
            throw new InvalidOperationException("Local automation requires at least one allowed root path.");
        }

        List<string>? roots;
        try
        {
            roots = JsonSerializer.Deserialize<List<string>>(allowedRootsJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Allowed local roots must be a JSON array of path strings.", ex);
        }

        if (roots is null || roots.Count == 0)
        {
            throw new InvalidOperationException("Local automation requires at least one allowed root path.");
        }

        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(LocalAutomationPathResolver.NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ValidateRequiredArguments(LocalAutomationAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        switch (action.Type)
        {
            case LocalAutomationActionType.Copy:
            case LocalAutomationActionType.Move:
            case LocalAutomationActionType.Rename:
                Require(action.Source, action.Type, "source");
                Require(action.Destination, action.Type, "destination");
                break;
            case LocalAutomationActionType.Delete:
            case LocalAutomationActionType.CreateDirectory:
                Require(action.Path, action.Type, "path");
                break;
            case LocalAutomationActionType.WriteTextFile:
                Require(action.Path, action.Type, "path");
                Require(action.Content, action.Type, "content");
                break;
            case LocalAutomationActionType.Zip:
            case LocalAutomationActionType.Unzip:
                Require(action.Source, action.Type, "source");
                Require(action.Destination, action.Type, "destination");
                break;
            case LocalAutomationActionType.RunPowerShell:
                Require(action.Script, action.Type, "script");
                break;
            default:
                throw new InvalidOperationException($"Unsupported local automation action type '{action.Type}'.");
        }
    }

    private static void ValidatePermissions(Agent agent, LocalAutomationAction action)
    {
        if (action.Type == LocalAutomationActionType.RunPowerShell && !agent.AllowPowerShellScripts)
        {
            throw new InvalidOperationException("PowerShell execution is not allowed for this agent.");
        }
    }

    private static void Require(string? value, LocalAutomationActionType actionType, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Action '{actionType}' requires '{propertyName}'.");
        }
    }

    private static IEnumerable<string> GetCandidatePaths(LocalAutomationAction action)
    {
        if (!string.IsNullOrWhiteSpace(action.Source))
        {
            yield return action.Source;
        }

        if (!string.IsNullOrWhiteSpace(action.Destination))
        {
            yield return action.Destination;
        }

        if (!string.IsNullOrWhiteSpace(action.Path))
        {
            yield return action.Path;
        }
    }

    private static void EnsureAllowed(string candidatePath, IReadOnlyList<string> allowedRoots)
    {
        var normalizedCandidate = LocalAutomationPathResolver.NormalizePath(candidatePath);

        var isAllowed = allowedRoots.Any(root =>
            normalizedCandidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        if (!isAllowed)
        {
            throw new InvalidOperationException($"Path '{candidatePath}' is outside an allowed root.");
        }
    }

    private static void ResolveActionPaths(LocalAutomationAction action, IReadOnlyList<string> allowedRoots)
    {
        switch (action.Type)
        {
            case LocalAutomationActionType.WriteTextFile when !string.IsNullOrWhiteSpace(action.Path):
                action.Path = ResolvePathAgainstAllowedRoots(action.Path, allowedRoots);
                break;
            case LocalAutomationActionType.Copy:
            case LocalAutomationActionType.Move:
            case LocalAutomationActionType.Rename:
                if (!string.IsNullOrWhiteSpace(action.Destination))
                {
                    action.Destination = ResolvePathAgainstAllowedRoots(action.Destination, allowedRoots);
                }
                break;
        }
    }

    private static string ResolvePathAgainstAllowedRoots(string candidatePath, IReadOnlyList<string> allowedRoots)
    {
        var normalizedCandidate = LocalAutomationPathResolver.NormalizePath(candidatePath);
        var parentDirectory = Path.GetDirectoryName(normalizedCandidate);
        if (string.IsNullOrWhiteSpace(parentDirectory) || Directory.Exists(parentDirectory))
        {
            return normalizedCandidate;
        }

        var targetFolderName = Path.GetFileName(parentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(targetFolderName))
        {
            return normalizedCandidate;
        }

        var matches = allowedRoots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.GetDirectories(root, targetFolderName, SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return matches.Count == 1
            ? Path.Combine(matches[0], Path.GetFileName(normalizedCandidate))
            : normalizedCandidate;
    }
}
