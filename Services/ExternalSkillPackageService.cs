using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class ExternalSkillSourceValidationResult
{
    public bool IsValid { get; set; }
    public string? ErrorMessage { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public string Repo { get; set; } = string.Empty;
    public string Ref { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public string RawUrl { get; set; } = string.Empty;
}

public class ExternalSkillPackageProvisionResult
{
    public ExternalSkillPackage Package { get; set; } = new();
    public string McpServerId { get; set; } = string.Empty;
}

public class ExternalSkillPackageHealthResult
{
    public bool Success { get; set; }
    public bool ToolNamesMatch { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> DeclaredTools { get; set; } = new();
    public List<string> DiscoveredTools { get; set; } = new();
}

public class ExternalSkillPackageService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    static ExternalSkillPackageService()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMcpConnectionProbe _mcpProbe;

    public ExternalSkillPackageService(
        IDbContextFactory<AppDbContext> factory,
        IHttpClientFactory httpClientFactory,
        IMcpConnectionProbe mcpProbe)
    {
        _factory = factory;
        _httpClientFactory = httpClientFactory;
        _mcpProbe = mcpProbe;
    }

    public async Task<List<ExternalSkillPackage>> GetAllAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.ExternalSkillPackages
            .OrderByDescending(p => p.UpdatedAt)
            .ToListAsync();
    }

    public async Task<ExternalSkillPackage?> GetByIdAsync(string packageId)
    {
        await using var db = _factory.CreateDbContext();
        return await db.ExternalSkillPackages.FirstOrDefaultAsync(p => p.Id == packageId);
    }

    public Task<ExternalSkillSourceValidationResult> ValidatePackageSourceAsync(string source, CancellationToken cancellationToken = default)
        => Task.FromResult(NormalizeSource(source));

    public async Task<ExternalSkillPackage> ImportFromGitHubAsync(string source, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeSource(source);
        if (!normalized.IsValid)
            throw new InvalidOperationException(normalized.ErrorMessage);

        if (normalized.ManifestPath.EndsWith("xpedeon-skill.json", StringComparison.OrdinalIgnoreCase))
            return await ImportManagedMcpPackageAsync(normalized, cancellationToken);

        if (normalized.ManifestPath.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase))
            return await ImportAgentSkillPackageAsync(normalized, cancellationToken);

        throw new InvalidOperationException("Unsupported GitHub package format. Expected xpedeon-skill.json or SKILL.md.");
    }

    public async Task<ExternalSkillPackage> ImportPackageManifestAsync(string source, CancellationToken cancellationToken = default)
    {
        return await ImportFromGitHubAsync(source, cancellationToken);
    }

    public async Task<ExternalSkillPackage> ApprovePackageAsync(string packageId, CancellationToken cancellationToken = default)
    {
        await using var db = _factory.CreateDbContext();
        var package = await db.ExternalSkillPackages.FirstOrDefaultAsync(p => p.Id == packageId, cancellationToken)
            ?? throw new InvalidOperationException("External skill package not found.");

        package.ApprovalStatus = ExternalSkillPackageApprovalStatus.Approved;
        package.UpdatedAt = DateTime.UtcNow;

        if (package.Kind == ExternalSkillPackageKind.AgentSkill)
        {
            package.InstallStatus = ExternalSkillPackageInstallStatus.Healthy;
            package.IsEnabled = true;
            await SetLinkedSkillEnabledStateAsync(db, package.Id, true, cancellationToken);
            RecordInstallEvent(db, package, null, "Skill package approved and enabled for use.");
            AddLog(db, $"Approved skill-only external package '{package.Name}'.", AgentLogLevel.Success);
            await db.SaveChangesAsync(cancellationToken);
            return package;
        }

        var manifest = ParseManifest(package.ManifestJson);
        package.InstallStatus = ExternalSkillPackageInstallStatus.Approved;
        package.IsEnabled = false;
        await SyncLinkedSkillsAsync(db, package, manifest, cancellationToken);

        RecordInstallEvent(db, package, null, "Package approved for provisioning.");
        AddLog(db, $"Approved external skill package '{package.Name}'.", AgentLogLevel.Success);
        await db.SaveChangesAsync(cancellationToken);
        return package;
    }

    public async Task<ExternalSkillPackageProvisionResult> ProvisionManagedMcpServerAsync(string packageId, CancellationToken cancellationToken = default)
    {
        await using var db = _factory.CreateDbContext();
        var package = await db.ExternalSkillPackages.FirstOrDefaultAsync(p => p.Id == packageId, cancellationToken)
            ?? throw new InvalidOperationException("External skill package not found.");

        if (package.Kind != ExternalSkillPackageKind.ManagedMcp)
            throw new InvalidOperationException("Only managed MCP packages can provision a managed MCP server.");

        if (package.ApprovalStatus != ExternalSkillPackageApprovalStatus.Approved)
            throw new InvalidOperationException("Package must be approved before provisioning.");

        var manifest = ParseManifest(package.ManifestJson);
        var server = await db.MCPServers.FirstOrDefaultAsync(s => s.ExternalSkillPackageId == package.Id, cancellationToken);

        if (server == null)
        {
            server = new MCPServer
            {
                Id = $"mcp-{Guid.NewGuid():N}"[..16],
                CreatedAt = DateTime.UtcNow
            };
            db.MCPServers.Add(server);
        }

        server.Name = $"{package.Name} MCP";
        server.Description = package.Description;
        server.TransportType = manifest.Runtime.TransportType;
        server.Endpoint = manifest.Runtime.Endpoint;
        server.IsManaged = true;
        server.ExternalSkillPackageId = package.Id;
        server.IsEnabled = false;
        server.IsGlobal = false;

        package.InstallStatus = ExternalSkillPackageInstallStatus.Provisioned;
        package.IsEnabled = false;
        package.UpdatedAt = DateTime.UtcNow;

        RecordInstallEvent(db, package, server.Id, "Managed MCP server provisioned.");
        AddLog(db, $"Provisioned managed MCP server for external package '{package.Name}'.", AgentLogLevel.Info);
        await db.SaveChangesAsync(cancellationToken);

        return new ExternalSkillPackageProvisionResult
        {
            Package = package,
            McpServerId = server.Id
        };
    }

    public async Task<ExternalSkillPackageHealthResult> CheckHealthAsync(string packageId, CancellationToken cancellationToken = default)
    {
        await using var db = _factory.CreateDbContext();
        var package = await db.ExternalSkillPackages.FirstOrDefaultAsync(p => p.Id == packageId, cancellationToken)
            ?? throw new InvalidOperationException("External skill package not found.");

        if (package.Kind != ExternalSkillPackageKind.ManagedMcp)
            throw new InvalidOperationException("Health checks apply only to managed MCP packages.");

        var server = await db.MCPServers.FirstOrDefaultAsync(s => s.ExternalSkillPackageId == package.Id, cancellationToken)
            ?? throw new InvalidOperationException("Managed MCP server has not been provisioned.");

        var testResult = await _mcpProbe.TestConnectionAsync(server, cancellationToken);
        var toolsResult = await _mcpProbe.ListToolsAsync(server, cancellationToken);

        var declaredTools = DeserializeList(package.DeclaredToolNamesJson)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var discoveredTools = toolsResult.Tools
            .Select(t => t.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var toolsMatch = declaredTools.Count == discoveredTools.Count &&
                         declaredTools.SequenceEqual(discoveredTools, StringComparer.OrdinalIgnoreCase);

        var success = testResult.Success && toolsResult.Success && toolsMatch;
        package.LastHealthCheckAt = DateTime.UtcNow;
        package.UpdatedAt = DateTime.UtcNow;
        package.IsEnabled = success;
        package.InstallStatus = success
            ? ExternalSkillPackageInstallStatus.Healthy
            : ExternalSkillPackageInstallStatus.HealthCheckFailed;
        server.IsEnabled = success;
        await SetLinkedSkillEnabledStateAsync(db, package.Id, success, cancellationToken);

        var message = success
            ? $"Health check passed for '{package.Name}'."
            : BuildFailureMessage(testResult, toolsResult, declaredTools, discoveredTools, toolsMatch);

        RecordInstallEvent(db, package, server.Id, message);
        AddLog(db, message, success ? AgentLogLevel.Success : AgentLogLevel.Warning);
        await db.SaveChangesAsync(cancellationToken);

        return new ExternalSkillPackageHealthResult
        {
            Success = success,
            ToolNamesMatch = toolsMatch,
            Message = message,
            DeclaredTools = declaredTools,
            DiscoveredTools = discoveredTools
        };
    }

    public async Task<ExternalSkillPackage> RefreshPackageAsync(string packageId, CancellationToken cancellationToken = default)
    {
        await using var db = _factory.CreateDbContext();
        var package = await db.ExternalSkillPackages.FirstOrDefaultAsync(p => p.Id == packageId, cancellationToken)
            ?? throw new InvalidOperationException("External skill package not found.");

        await DisableLinkedArtifactsAsync(db, package.Id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return await ImportFromGitHubAsync(package.SourceUrl, cancellationToken);
    }

    public async Task<ExternalSkillPackage> UninstallPackageAsync(string packageId, CancellationToken cancellationToken = default)
    {
        await using var db = _factory.CreateDbContext();
        var package = await db.ExternalSkillPackages.FirstOrDefaultAsync(p => p.Id == packageId, cancellationToken)
            ?? throw new InvalidOperationException("External skill package not found.");

        await DisableLinkedArtifactsAsync(db, package.Id, cancellationToken);
        package.IsEnabled = false;
        package.InstallStatus = ExternalSkillPackageInstallStatus.Uninstalled;
        package.UpdatedAt = DateTime.UtcNow;

        RecordInstallEvent(db, package, null, "Package uninstalled and managed artifacts disabled.");
        AddLog(db, $"Uninstalled external skill package '{package.Name}'.", AgentLogLevel.Warning);
        await db.SaveChangesAsync(cancellationToken);
        return package;
    }

    private async Task<ExternalSkillPackage> ImportManagedMcpPackageAsync(ExternalSkillSourceValidationResult normalized, CancellationToken cancellationToken)
    {
        var manifestJson = await DownloadTextAsync(normalized.RawUrl, cancellationToken);
        var manifest = ParseManifest(manifestJson);
        var contentHash = ComputeHash(manifestJson);
        var now = DateTime.UtcNow;

        await using var db = _factory.CreateDbContext();
        var package = await FindOrCreatePackageAsync(db, normalized, now, cancellationToken);
        var contentChanged = !string.Equals(package.ContentHash, contentHash, StringComparison.Ordinal);

        package.Kind = ExternalSkillPackageKind.ManagedMcp;
        package.PackageKey = manifest.PackageId;
        package.Name = manifest.Name;
        package.Version = manifest.Version;
        package.Description = manifest.Description;
        package.ContentHash = contentHash;
        package.ManifestJson = manifestJson;
        package.RuntimeTransportType = manifest.Runtime.TransportType;
        package.RuntimeEndpoint = manifest.Runtime.Endpoint;
        package.RequiredSecretNamesJson = SerializeList(manifest.RequiredSecrets);
        package.PermissionHintsJson = SerializeList(manifest.PermissionHints);
        package.DeclaredToolNamesJson = SerializeList(manifest.Tools.Select(t => t.Name));
        package.ImportError = null;
        package.LastSyncAt = now;
        package.UpdatedAt = now;
        package.IsEnabled = false;

        if (contentChanged)
        {
            package.ApprovalStatus = ExternalSkillPackageApprovalStatus.PendingReview;
            package.InstallStatus = ExternalSkillPackageInstallStatus.Draft;
            await DisableLinkedArtifactsAsync(db, package.Id, cancellationToken);
        }

        RecordInstallEvent(db, package, null, $"Manifest imported from {normalized.Repo}@{normalized.Ref}.");
        AddLog(db, $"Imported external MCP package '{package.Name}' from GitHub.", AgentLogLevel.Info);
        await db.SaveChangesAsync(cancellationToken);
        return package;
    }

    private async Task<ExternalSkillPackage> ImportAgentSkillPackageAsync(ExternalSkillSourceValidationResult normalized, CancellationToken cancellationToken)
    {
        var markdown = await DownloadTextAsync(normalized.RawUrl, cancellationToken);
        var parsedSkill = ParseSkillMarkdown(markdown, normalized);
        var contentHash = ComputeHash(markdown);
        var now = DateTime.UtcNow;

        await using var db = _factory.CreateDbContext();
        var package = await FindOrCreatePackageAsync(db, normalized, now, cancellationToken);

        package.Kind = ExternalSkillPackageKind.AgentSkill;
        package.PackageKey = parsedSkill.PackageKey;
        package.Name = parsedSkill.Name;
        package.Version = "imported";
        package.Description = parsedSkill.Description;
        package.ContentHash = contentHash;
        package.ManifestJson = JsonSerializer.Serialize(new
        {
            format = "agent-skill",
            sourcePath = normalized.ManifestPath,
            metadata = new { parsedSkill.Name, parsedSkill.Description },
            content = markdown
        }, JsonOptions);
        package.RuntimeTransportType = MCPTransportType.Http;
        package.RuntimeEndpoint = "Skill-only import";
        package.RequiredSecretNamesJson = "[]";
        package.PermissionHintsJson = "[]";
        package.DeclaredToolNamesJson = "[]";
        package.ApprovalStatus = ExternalSkillPackageApprovalStatus.PendingReview;
        package.InstallStatus = ExternalSkillPackageInstallStatus.Draft;
        package.ImportError = null;
        package.LastSyncAt = now;
        package.UpdatedAt = now;
        package.IsEnabled = false;

        await DisableLinkedArtifactsAsync(db, package.Id, cancellationToken);
        await SyncSkillOnlyPackageAsync(db, package, parsedSkill, cancellationToken);

        RecordInstallEvent(db, package, null, $"Skill repo imported from {normalized.Repo}@{normalized.Ref}.");
        AddLog(db, $"Imported external skill repo '{package.Name}' from GitHub.", AgentLogLevel.Info);
        await db.SaveChangesAsync(cancellationToken);
        return package;
    }

    private async Task<ExternalSkillPackage> FindOrCreatePackageAsync(
        AppDbContext db,
        ExternalSkillSourceValidationResult normalized,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var package = await db.ExternalSkillPackages.FirstOrDefaultAsync(
            p => p.SourceRepo == normalized.Repo &&
                 p.SourceRef == normalized.Ref &&
                 p.ManifestPath == normalized.ManifestPath,
            cancellationToken);

        if (package == null)
        {
            package = new ExternalSkillPackage
            {
                Id = $"pkg-{Guid.NewGuid():N}"[..16],
                CreatedAt = now
            };
            db.ExternalSkillPackages.Add(package);
        }

        package.SourceUrl = normalized.SourceUrl;
        package.SourceRepo = normalized.Repo;
        package.SourceRef = normalized.Ref;
        package.ManifestPath = normalized.ManifestPath;
        package.RawSourceUrl = normalized.RawUrl;
        return package;
    }

    private async Task<string> DownloadTextAsync(string rawUrl, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(ExternalSkillPackageService));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XpedeonAgentMissionControl/1.0");

        using var response = await client.GetAsync(rawUrl, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Unable to download package source. GitHub returned {(int)response.StatusCode}.");
        if (body.Length > 500_000)
            throw new InvalidOperationException("Package content is too large. The maximum supported size is 500 KB.");

        return body;
    }

    private static ExternalSkillSourceValidationResult NormalizeSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new ExternalSkillSourceValidationResult { ErrorMessage = "GitHub source is required." };

        var trimmed = source.Trim();
        var blobMatch = Regex.Match(trimmed, @"^https://github\.com/(?<owner>[^/\s]+)/(?<repo>[^/\s]+)/blob/(?<ref>[^/\s]+)/(?<path>.+)$", RegexOptions.IgnoreCase);
        if (blobMatch.Success)
        {
            return BuildValidResult(
                trimmed,
                $"{blobMatch.Groups["owner"].Value}/{blobMatch.Groups["repo"].Value}",
                blobMatch.Groups["ref"].Value,
                blobMatch.Groups["path"].Value);
        }

        var rawMatch = Regex.Match(trimmed, @"^https://raw\.githubusercontent\.com/(?<owner>[^/\s]+)/(?<repo>[^/\s]+)/(?<ref>[^/\s]+)/(?<path>.+)$", RegexOptions.IgnoreCase);
        if (rawMatch.Success)
        {
            return BuildValidResult(
                trimmed,
                $"{rawMatch.Groups["owner"].Value}/{rawMatch.Groups["repo"].Value}",
                rawMatch.Groups["ref"].Value,
                rawMatch.Groups["path"].Value);
        }

        var shorthandMatch = Regex.Match(trimmed, @"^(?<owner>[^/\s]+)/(?<repo>[^/\s]+)/(?<path>.+)@(?<ref>[^@\s]+)$", RegexOptions.IgnoreCase);
        if (shorthandMatch.Success)
        {
            return BuildValidResult(
                trimmed,
                $"{shorthandMatch.Groups["owner"].Value}/{shorthandMatch.Groups["repo"].Value}",
                shorthandMatch.Groups["ref"].Value,
                shorthandMatch.Groups["path"].Value);
        }

        return new ExternalSkillSourceValidationResult
        {
            ErrorMessage = "Only GitHub blob URLs, raw URLs, or owner/repo/path@ref references are supported."
        };
    }

    private static ExternalSkillSourceValidationResult BuildValidResult(string sourceUrl, string repo, string gitRef, string manifestPath)
        => new()
        {
            IsValid = true,
            SourceUrl = sourceUrl,
            Repo = repo,
            Ref = gitRef,
            ManifestPath = manifestPath,
            RawUrl = $"https://raw.githubusercontent.com/{repo}/{gitRef}/{manifestPath}"
        };

    private static ExternalSkillManifest ParseManifest(string manifestJson)
    {
        ExternalSkillManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ExternalSkillManifest>(manifestJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Manifest is not valid JSON: {ex.Message}");
        }

        if (manifest == null)
            throw new InvalidOperationException("Manifest content is empty.");
        if (string.IsNullOrWhiteSpace(manifest.PackageId))
            throw new InvalidOperationException("Manifest packageId is required.");
        if (string.IsNullOrWhiteSpace(manifest.Name))
            throw new InvalidOperationException("Manifest name is required.");
        if (string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidOperationException("Manifest version is required.");
        if (string.IsNullOrWhiteSpace(manifest.Description))
            throw new InvalidOperationException("Manifest description is required.");
        if (string.IsNullOrWhiteSpace(manifest.Runtime.Endpoint))
            throw new InvalidOperationException("Manifest runtime endpoint is required.");

        manifest.Tools = manifest.Tools
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (!manifest.Tools.Any())
            throw new InvalidOperationException("Manifest must declare at least one tool.");

        manifest.RequiredSecrets = NormalizeStrings(manifest.RequiredSecrets);
        manifest.PermissionHints = NormalizeStrings(manifest.PermissionHints);

        foreach (var skill in manifest.DefaultSkills)
        {
            if (string.IsNullOrWhiteSpace(skill.Name))
                throw new InvalidOperationException("Each default skill must include a name.");
            if (string.IsNullOrWhiteSpace(skill.Description))
                throw new InvalidOperationException($"Default skill '{skill.Name}' must include a description.");

            skill.AllowedMcpToolNames = NormalizeStrings(skill.AllowedMcpToolNames);
        }

        return manifest;
    }

    private async Task SyncLinkedSkillsAsync(AppDbContext db, ExternalSkillPackage package, ExternalSkillManifest manifest, CancellationToken cancellationToken)
    {
        var existingSkills = await db.SkillDefinitions
            .Where(s => s.ExternalSkillPackageId == package.Id)
            .ToListAsync(cancellationToken);

        foreach (var manifestSkill in manifest.DefaultSkills)
        {
            if (!Enum.TryParse<SkillCategory>(manifestSkill.Category, true, out var category))
                category = SkillCategory.General;

            var skill = existingSkills.FirstOrDefault(s => s.Name.Equals(manifestSkill.Name, StringComparison.OrdinalIgnoreCase));
            if (skill == null)
            {
                skill = new SkillDefinition
                {
                    Id = $"skill-{Guid.NewGuid():N}"[..18],
                    CreatedAt = DateTime.UtcNow
                };
                db.SkillDefinitions.Add(skill);
                existingSkills.Add(skill);
            }

            skill.ExternalSkillPackageId = package.Id;
            skill.Name = manifestSkill.Name.Trim();
            skill.Category = category;
            skill.Description = manifestSkill.Description.Trim();
            skill.PromptSnippet = manifestSkill.PromptSnippet?.Trim() ?? string.Empty;
            skill.AllowedMcpToolNamesJson = SerializeList(manifestSkill.AllowedMcpToolNames);
            skill.PreferredModelName = manifestSkill.PreferredModelName;
            skill.MaxToolCalls = manifestSkill.MaxToolCalls;
            skill.RequiresApproval = manifestSkill.RequiresApproval;
            skill.IsBuiltIn = false;
            skill.IsEnabled = false;
        }

        var validNames = manifest.DefaultSkills
            .Select(s => s.Name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var skill in existingSkills.Where(s => !validNames.Contains(s.Name)))
            skill.IsEnabled = false;
    }

    private async Task SyncSkillOnlyPackageAsync(AppDbContext db, ExternalSkillPackage package, ParsedAgentSkill parsedSkill, CancellationToken cancellationToken)
    {
        var existingSkills = await db.SkillDefinitions
            .Where(s => s.ExternalSkillPackageId == package.Id)
            .ToListAsync(cancellationToken);

        var skill = existingSkills.FirstOrDefault();
        if (skill == null)
        {
            skill = new SkillDefinition
            {
                Id = $"skill-{Guid.NewGuid():N}"[..18],
                CreatedAt = DateTime.UtcNow
            };
            db.SkillDefinitions.Add(skill);
        }

        skill.ExternalSkillPackageId = package.Id;
        skill.Name = parsedSkill.Name;
        skill.Category = SkillCategory.General;
        skill.Description = parsedSkill.Description;
        skill.PromptSnippet = parsedSkill.PromptSnippet;
        skill.AllowedMcpToolNamesJson = "[]";
        skill.PreferredProviderId = null;
        skill.PreferredModelName = null;
        skill.MaxToolCalls = null;
        skill.RequiresApproval = false;
        skill.IsBuiltIn = false;
        skill.IsEnabled = false;
    }

    private async Task DisableLinkedArtifactsAsync(AppDbContext db, string packageId, CancellationToken cancellationToken)
    {
        await SetLinkedSkillEnabledStateAsync(db, packageId, false, cancellationToken);

        var linkedServers = await db.MCPServers.Where(s => s.ExternalSkillPackageId == packageId).ToListAsync(cancellationToken);
        foreach (var server in linkedServers)
            server.IsEnabled = false;
    }

    private static async Task SetLinkedSkillEnabledStateAsync(
        AppDbContext db,
        string packageId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var linkedSkills = await db.SkillDefinitions
            .Where(s => s.ExternalSkillPackageId == packageId)
            .ToListAsync(cancellationToken);

        foreach (var skill in linkedSkills)
            skill.IsEnabled = isEnabled;
    }

    private static void RecordInstallEvent(AppDbContext db, ExternalSkillPackage package, string? mcpServerId, string notes)
    {
        db.ExternalSkillPackageInstalls.Add(new ExternalSkillPackageInstall
        {
            Id = $"pkginst-{Guid.NewGuid():N}"[..18],
            ExternalSkillPackageId = package.Id,
            Version = package.Version,
            ApprovalStatus = package.ApprovalStatus,
            InstallStatus = package.InstallStatus,
            ProvisionedMcpServerId = mcpServerId,
            Notes = notes,
            CreatedAt = DateTime.UtcNow
        });
    }

    private static void AddLog(AppDbContext db, string message, AgentLogLevel level)
    {
        db.Logs.Add(new LogEntry
        {
            AgentName = "External Packages",
            Message = message,
            Level = level,
            Timestamp = DateTime.UtcNow
        });
    }

    private static string BuildFailureMessage(
        MCPConnectionTestResult testResult,
        MCPToolDiscoveryResult toolsResult,
        IReadOnlyCollection<string> declaredTools,
        IReadOnlyCollection<string> discoveredTools,
        bool toolsMatch)
    {
        if (!testResult.Success)
            return testResult.Message;
        if (!toolsResult.Success)
            return toolsResult.Message;
        if (!toolsMatch)
            return $"Declared tools [{string.Join(", ", declaredTools)}] do not match discovered tools [{string.Join(", ", discoveredTools)}].";

        return "Health check failed.";
    }

    private static string SerializeList(IEnumerable<string> values)
        => JsonSerializer.Serialize(NormalizeStrings(values));

    private static List<string> DeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<string>();

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static List<string> NormalizeStrings(IEnumerable<string> values)
        => values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string ComputeHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }

    private static ParsedAgentSkill ParseSkillMarkdown(string markdown, ExternalSkillSourceValidationResult normalized)
    {
        var frontMatter = Regex.Match(markdown, @"\A---\s*(?<body>.*?)\s*---", RegexOptions.Singleline);
        var name = normalized.Repo.Split('/').Last();
        string? description = null;

        if (frontMatter.Success)
        {
            var lines = frontMatter.Groups["body"].Value
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                var parts = line.Split(':', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2)
                    continue;

                if (parts[0].Equals("name", StringComparison.OrdinalIgnoreCase))
                    name = parts[1].Trim().Trim('"');
                else if (parts[0].Equals("description", StringComparison.OrdinalIgnoreCase))
                    description = parts[1].Trim().Trim('"');
            }
        }

        description ??= ExtractFirstParagraph(markdown) ?? $"Imported agent skill from {normalized.Repo}.";

        return new ParsedAgentSkill
        {
            PackageKey = Slugify(name),
            Name = name,
            Description = description,
            PromptSnippet = BuildPromptSnippet(markdown)
        };
    }

    private static string? ExtractFirstParagraph(string markdown)
    {
        var stripped = Regex.Replace(markdown, @"\A---.*?---", string.Empty, RegexOptions.Singleline).Trim();
        return stripped
            .Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Replace("#", string.Empty).Trim())
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
    }

    private static string BuildPromptSnippet(string markdown)
    {
        var body = Regex.Replace(markdown, @"\A---.*?---", string.Empty, RegexOptions.Singleline).Trim();
        return body.Length <= 1200 ? body : body[..1200];
    }

    private static string Slugify(string value)
    {
        var slug = Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "imported-skill" : slug;
    }

    private sealed class ExternalSkillManifest
    {
        public string PackageId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public ExternalSkillManifestRuntime Runtime { get; set; } = new();
        public List<ExternalSkillManifestTool> Tools { get; set; } = new();
        public List<string> RequiredSecrets { get; set; } = new();
        public List<string> PermissionHints { get; set; } = new();
        public List<ExternalSkillManifestSkill> DefaultSkills { get; set; } = new();
    }

    private sealed class ExternalSkillManifestRuntime
    {
        public MCPTransportType TransportType { get; set; }
        public string Endpoint { get; set; } = string.Empty;
    }

    private sealed class ExternalSkillManifestTool
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    private sealed class ExternalSkillManifestSkill
    {
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = nameof(SkillCategory.General);
        public string Description { get; set; } = string.Empty;
        public string? PromptSnippet { get; set; }
        public List<string> AllowedMcpToolNames { get; set; } = new();
        public string? PreferredModelName { get; set; }
        public int? MaxToolCalls { get; set; }
        public bool RequiresApproval { get; set; }
    }

    private sealed class ParsedAgentSkill
    {
        public string PackageKey { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string PromptSnippet { get; set; } = string.Empty;
    }
}
