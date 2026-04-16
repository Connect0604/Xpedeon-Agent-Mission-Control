namespace XpedeonAgentMissionControl.Services;

internal static class LocalAutomationPathResolver
{
    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var expanded = Environment.ExpandEnvironmentVariables(path);
        return Path.GetFullPath(expanded)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string? NormalizePathOrNull(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : NormalizePath(path);
}
