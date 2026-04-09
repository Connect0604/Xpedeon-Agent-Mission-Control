namespace XpedeonAgentMissionControl.Models;

public enum ExternalSkillPackageApprovalStatus
{
    PendingReview,
    Approved,
    Rejected
}

public enum ExternalSkillPackageInstallStatus
{
    Draft,
    Approved,
    Provisioned,
    Healthy,
    HealthCheckFailed,
    Uninstalled
}

public enum ExternalSkillPackageKind
{
    ManagedMcp,
    AgentSkill
}

public class ExternalSkillPackage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PackageKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string SourceRepo { get; set; } = string.Empty;
    public string SourceRef { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public string RawSourceUrl { get; set; } = string.Empty;
    public ExternalSkillPackageKind Kind { get; set; } = ExternalSkillPackageKind.ManagedMcp;
    public string ContentHash { get; set; } = string.Empty;
    public string ManifestJson { get; set; } = string.Empty;
    public MCPTransportType RuntimeTransportType { get; set; } = MCPTransportType.Http;
    public string RuntimeEndpoint { get; set; } = string.Empty;
    public string RequiredSecretNamesJson { get; set; } = "[]";
    public string PermissionHintsJson { get; set; } = "[]";
    public string DeclaredToolNamesJson { get; set; } = "[]";
    public ExternalSkillPackageApprovalStatus ApprovalStatus { get; set; } = ExternalSkillPackageApprovalStatus.PendingReview;
    public ExternalSkillPackageInstallStatus InstallStatus { get; set; } = ExternalSkillPackageInstallStatus.Draft;
    public bool IsEnabled { get; set; }
    public string? ImportError { get; set; }
    public DateTime LastSyncAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastHealthCheckAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<ExternalSkillPackageInstall> Installs { get; set; } = new();
}

public class ExternalSkillPackageInstall
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ExternalSkillPackageId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public ExternalSkillPackageApprovalStatus ApprovalStatus { get; set; }
    public ExternalSkillPackageInstallStatus InstallStatus { get; set; }
    public string? ProvisionedMcpServerId { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ExternalSkillPackage? ExternalSkillPackage { get; set; }
    public MCPServer? ProvisionedMcpServer { get; set; }
}
