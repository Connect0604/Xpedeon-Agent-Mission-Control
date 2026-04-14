using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public sealed class LocalCapabilityMatcher
{
    public LocalCapability? Match(
        LocalCapabilityInvocation invocation,
        IReadOnlyCollection<LocalCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(capabilities);

        var requestedName = Normalize(invocation.CapabilityName);
        if (string.IsNullOrWhiteSpace(requestedName) || capabilities.Count == 0)
        {
            return null;
        }

        return capabilities
            .Where(capability => capability.IsActive)
            .FirstOrDefault(capability =>
                Matches(capability.Name, requestedName) ||
                Matches(capability.DisplayName, requestedName) ||
                Matches(capability.HandlerKey, requestedName));
    }

    private static bool Matches(string? value, string normalizedRequestedName)
        => Normalize(value) == normalizedRequestedName;

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
