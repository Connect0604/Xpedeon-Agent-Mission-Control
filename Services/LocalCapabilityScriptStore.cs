using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public sealed class LocalCapabilityScriptStore
{
    private readonly string _contentRootPath;

    public LocalCapabilityScriptStore()
        : this(AppContext.BaseDirectory)
    {
    }

    public LocalCapabilityScriptStore(string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(contentRootPath))
        {
            throw new InvalidOperationException("A content root path is required for local capability script storage.");
        }

        _contentRootPath = Path.GetFullPath(contentRootPath);
    }

    public Task<string> SaveDraftAsync(LocalCapabilityDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return WriteScriptAsync(draft.Name, draft.Script, "Drafts", 1, cancellationToken);
    }

    public Task<string> SaveAsync(LocalCapability capability, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capability);
        return WriteScriptAsync(capability.Name, capability.ScriptContent, "Scripts", capability.Version, cancellationToken);
    }

    private async Task<string> WriteScriptAsync(
        string name,
        string? script,
        string folderName,
        int version,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            throw new InvalidOperationException("Local capability script content is required.");
        }

        var folder = Path.Combine(_contentRootPath, "LocalCapabilities", folderName);
        Directory.CreateDirectory(folder);

        var safeName = SanitizeFileName(name);
        var fileName = $"{safeName}.v{Math.Max(1, version)}.{DateTime.UtcNow:yyyyMMddHHmmss}.{Guid.NewGuid():N}.ps1";
        var path = Path.Combine(folder, fileName);

        await File.WriteAllTextAsync(path, script, cancellationToken);
        return path;
    }

    private static string SanitizeFileName(string? value)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? "generated-capability" : value.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(candidate.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "generated-capability" : cleaned;
    }
}
