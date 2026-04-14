using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public sealed class LocalCapabilityService
{
    private static readonly DateTime BuiltInCreatedAt = new(2026, 4, 11, 0, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyList<LocalCapability> BuiltIns = new[]
    {
        CreateBuiltIn(
            name: "createDirectory",
            displayName: "Create Directory",
            category: LocalCapabilityCategory.Write,
            handlerKey: "createDirectory",
            description: "Create a directory at an approved path.",
            inputSchemaJson: """
            {"path":"string"}
            """,
            outputSchemaJson: """
            {"path":"string"}
            """,
            requiresApproval: true),
        CreateBuiltIn(
            name: "listFiles",
            displayName: "List Files",
            category: LocalCapabilityCategory.Read,
            handlerKey: "listFiles",
            description: "List files in a folder using an optional glob pattern.",
            inputSchemaJson: """
            {"rootPath":"string","pattern":"string","recursive":"boolean"}
            """,
            outputSchemaJson: """
            {"files":"string[]"}
            """),
        CreateBuiltIn(
            name: "countFiles",
            displayName: "Count Files",
            category: LocalCapabilityCategory.Query,
            handlerKey: "countFiles",
            description: "Count files in a folder using an optional glob pattern.",
            inputSchemaJson: """
            {"rootPath":"string","pattern":"string","recursive":"boolean"}
            """,
            outputSchemaJson: """
            {"count":"number"}
            """),
        CreateBuiltIn(
            name: "getFileMetadata",
            displayName: "Get File Metadata",
            category: LocalCapabilityCategory.Read,
            handlerKey: "getFileMetadata",
            description: "Read file or directory metadata without modifying content.",
            inputSchemaJson: """
            {"path":"string"}
            """,
            outputSchemaJson: """
            {"path":"string","exists":"boolean","isDirectory":"boolean","sizeBytes":"number"}
            """),
        CreateBuiltIn(
            name: "readTextFile",
            displayName: "Read Text File",
            category: LocalCapabilityCategory.Read,
            handlerKey: "readTextFile",
            description: "Read a text file from an approved path.",
            inputSchemaJson: """
            {"path":"string"}
            """,
            outputSchemaJson: """
            {"content":"string"}
            """),
        CreateBuiltIn(
            name: "writeTextFile",
            displayName: "Write Text File",
            category: LocalCapabilityCategory.Write,
            handlerKey: "writeTextFile",
            description: "Write a text file to an approved path.",
            inputSchemaJson: """
            {"path":"string","content":"string","createParents":"boolean"}
            """,
            outputSchemaJson: """
            {"path":"string"}
            """,
            requiresApproval: true),
        CreateBuiltIn(
            name: "copy",
            displayName: "Copy File",
            category: LocalCapabilityCategory.Write,
            handlerKey: "copy",
            description: "Copy a file to another approved location.",
            inputSchemaJson: """
            {"source":"string","destination":"string","overwrite":"boolean"}
            """,
            outputSchemaJson: """
            {"destination":"string"}
            """,
            requiresApproval: true),
        CreateBuiltIn(
            name: "move",
            displayName: "Move File",
            category: LocalCapabilityCategory.Write,
            handlerKey: "move",
            description: "Move a file to another approved location.",
            inputSchemaJson: """
            {"source":"string","destination":"string","overwrite":"boolean"}
            """,
            outputSchemaJson: """
            {"destination":"string"}
            """,
            requiresApproval: true),
        CreateBuiltIn(
            name: "rename",
            displayName: "Rename File",
            category: LocalCapabilityCategory.Write,
            handlerKey: "rename",
            description: "Rename a file within an approved location.",
            inputSchemaJson: """
            {"source":"string","destination":"string","overwrite":"boolean"}
            """,
            outputSchemaJson: """
            {"destination":"string"}
            """,
            requiresApproval: true)
    };

    private readonly IDbContextFactory<AppDbContext> _factory;

    public LocalCapabilityService(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<List<LocalCapability>> GetAllAsync()
    {
        await using var db = _factory.CreateDbContext();
        await EnsureBuiltInsAsync(db);
        return await db.LocalCapabilities
            .OrderByDescending(capability => capability.ExecutionType == LocalCapabilityExecutionType.BuiltIn)
            .ThenBy(capability => capability.Category)
            .ThenBy(capability => capability.DisplayName)
            .ToListAsync();
    }

    public async Task<LocalCapability?> GetByNameAsync(string name)
    {
        await using var db = _factory.CreateDbContext();
        await EnsureBuiltInsAsync(db);
        return await db.LocalCapabilities.FirstOrDefaultAsync(capability => capability.Name == name);
    }

    public async Task EnsureBuiltInsAsync()
    {
        await using var db = _factory.CreateDbContext();
        await EnsureBuiltInsAsync(db);
    }

    private static async Task EnsureBuiltInsAsync(AppDbContext db)
    {
        var existing = await db.LocalCapabilities.ToDictionaryAsync(capability => capability.Name, StringComparer.OrdinalIgnoreCase);

        var changed = false;
        foreach (var builtIn in BuiltIns)
        {
            if (existing.TryGetValue(builtIn.Name, out var current))
            {
                changed |= UpdateBuiltIn(current, builtIn);
            }
            else
            {
                db.LocalCapabilities.Add(CloneBuiltIn(builtIn));
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync();
        }
    }

    public async Task<LocalCapability> SaveGeneratedAsync(
        LocalCapabilityDraft draft,
        string scriptPath,
        string? allowedRootsJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            throw new InvalidOperationException("A saved script path is required for generated capabilities.");
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var existingGenerated = await db.LocalCapabilities
            .Where(capability => capability.IsGenerated && capability.Name == draft.Name)
            .OrderByDescending(capability => capability.Version)
            .ToListAsync(cancellationToken);

        foreach (var existing in existingGenerated.Where(capability => capability.IsActive))
        {
            existing.IsActive = false;
        }

        var nextVersion = existingGenerated.Count == 0
            ? 1
            : existingGenerated.Max(capability => capability.Version) + 1;

        var capability = new LocalCapability
        {
            Id = BuildGeneratedId(draft.Name, nextVersion),
            Name = draft.Name,
            DisplayName = draft.Name,
            Description = draft.Description,
            Category = ParseCategory(draft.Category),
            ExecutionType = LocalCapabilityExecutionType.PowerShell,
            ScriptPath = scriptPath,
            ScriptContent = draft.Script,
            InputSchemaJson = JsonSerializer.Serialize(draft.Inputs),
            OutputSchemaJson = "{}",
            AllowedRootsJson = allowedRootsJson,
            RequiresApproval = false,
            IsGenerated = true,
            IsActive = true,
            Version = nextVersion,
            CreatedAt = DateTime.UtcNow
        };

        db.LocalCapabilities.Add(capability);
        await db.SaveChangesAsync(cancellationToken);
        return capability;
    }

    private static LocalCapability CreateBuiltIn(
        string name,
        string displayName,
        LocalCapabilityCategory category,
        string handlerKey,
        string description,
        string inputSchemaJson,
        string outputSchemaJson,
        bool requiresApproval = false)
    {
        return new LocalCapability
        {
            Id = $"cap-{name}",
            Name = name,
            DisplayName = displayName,
            Description = description,
            Category = category,
            ExecutionType = LocalCapabilityExecutionType.BuiltIn,
            HandlerKey = handlerKey,
            InputSchemaJson = inputSchemaJson.Trim(),
            OutputSchemaJson = outputSchemaJson.Trim(),
            RequiresApproval = requiresApproval,
            IsGenerated = false,
            IsActive = true,
            Version = 1,
            CreatedAt = BuiltInCreatedAt
        };
    }

    private static LocalCapability CloneBuiltIn(LocalCapability source)
    {
        return new LocalCapability
        {
            Id = source.Id,
            Name = source.Name,
            DisplayName = source.DisplayName,
            Description = source.Description,
            Category = source.Category,
            ExecutionType = source.ExecutionType,
            HandlerKey = source.HandlerKey,
            ScriptPath = source.ScriptPath,
            ScriptContent = source.ScriptContent,
            InputSchemaJson = source.InputSchemaJson,
            OutputSchemaJson = source.OutputSchemaJson,
            AllowedRootsJson = source.AllowedRootsJson,
            RequiresApproval = source.RequiresApproval,
            IsGenerated = source.IsGenerated,
            IsActive = source.IsActive,
            Version = source.Version,
            CreatedAt = source.CreatedAt,
            LastUsedAt = source.LastUsedAt,
            SuccessCount = source.SuccessCount,
            FailureCount = source.FailureCount
        };
    }

    private static bool UpdateBuiltIn(LocalCapability current, LocalCapability source)
    {
        var changed = false;

        changed |= SetIfDifferent(current.DisplayName, source.DisplayName, value => current.DisplayName = value);
        changed |= SetIfDifferent(current.Description, source.Description, value => current.Description = value);
        changed |= SetIfDifferent(current.Category, source.Category, value => current.Category = value);
        changed |= SetIfDifferent(current.ExecutionType, source.ExecutionType, value => current.ExecutionType = value);
        changed |= SetIfDifferent(current.HandlerKey, source.HandlerKey, value => current.HandlerKey = value);
        changed |= SetIfDifferent(current.ScriptPath, source.ScriptPath, value => current.ScriptPath = value);
        changed |= SetIfDifferent(current.ScriptContent, source.ScriptContent, value => current.ScriptContent = value);
        changed |= SetIfDifferent(current.InputSchemaJson, source.InputSchemaJson, value => current.InputSchemaJson = value);
        changed |= SetIfDifferent(current.OutputSchemaJson, source.OutputSchemaJson, value => current.OutputSchemaJson = value);
        changed |= SetIfDifferent(current.AllowedRootsJson, source.AllowedRootsJson, value => current.AllowedRootsJson = value);
        changed |= SetIfDifferent(current.RequiresApproval, source.RequiresApproval, value => current.RequiresApproval = value);
        changed |= SetIfDifferent(current.IsGenerated, source.IsGenerated, value => current.IsGenerated = value);
        changed |= SetIfDifferent(current.IsActive, source.IsActive, value => current.IsActive = value);

        return changed;
    }

    private static bool SetIfDifferent<T>(T currentValue, T nextValue, Action<T> assign)
    {
        if (EqualityComparer<T>.Default.Equals(currentValue, nextValue))
        {
            return false;
        }

        assign(nextValue);
        return true;
    }

    private static LocalCapabilityCategory ParseCategory(string? value)
    {
        if (Enum.TryParse<LocalCapabilityCategory>(value, ignoreCase: true, out var category))
        {
            return category;
        }

        return LocalCapabilityCategory.Query;
    }

    private static string BuildGeneratedId(string name, int version)
    {
        var safeName = new string((name ?? "generated")
            .Where(char.IsLetterOrDigit)
            .ToArray());
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "generated";
        }

        var candidate = $"cap-{safeName}-v{version}";
        return candidate.Length <= 64
            ? candidate
            : candidate[..64];
    }
}
