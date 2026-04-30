namespace XpedeonAgentMissionControl.Models;

/// <summary>
/// Audit log entry tracking all CRUD operations for compliance and debugging
/// </summary>
public class AuditLog
{
    public int Id { get; set; }

    /// <summary>
    /// User who performed the action (null = system)
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Action performed: Create, Read, Update, Delete, Execute, etc.
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Entity type (e.g., "Agent", "Task", "LLMProvider")
    /// </summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>
    /// Entity ID (e.g., agentId = 42)
    /// </summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>
    /// Entity name/title for human readability (e.g., "Claude Agent #42")
    /// </summary>
    public string? EntityName { get; set; }

    /// <summary>
    /// JSON snapshot of entity before change (null for Create/Read/Delete)
    /// </summary>
    public string? BeforeJson { get; set; }

    /// <summary>
    /// JSON snapshot of entity after change (null for Delete/Read)
    /// </summary>
    public string? AfterJson { get; set; }

    /// <summary>
    /// Human-readable change description (e.g., "Name: 'Old' → 'New'")
    /// </summary>
    public string? ChangeDescription { get; set; }

    /// <summary>
    /// When the action occurred
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Client IP address (for security tracking)
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// HTTP user agent (for tracking automation/tools)
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// Request path/endpoint (e.g., "/api/agents/42")
    /// </summary>
    public string? RequestPath { get; set; }

    /// <summary>
    /// HTTP method (GET, POST, PUT, DELETE, etc.)
    /// </summary>
    public string? HttpMethod { get; set; }

    /// <summary>
    /// HTTP response status code
    /// </summary>
    public int? ResponseStatusCode { get; set; }

    /// <summary>
    /// Error message if action failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Additional metadata as JSON (custom data per entity)
    /// </summary>
    public string? MetadataJson { get; set; }

    // Computed properties
    public bool IsSuccess => ResponseStatusCode == null || ResponseStatusCode < 400;
    public bool IsFailure => !IsSuccess;

    /// <summary>
    /// Summary for display in UI/reports
    /// </summary>
    public string Summary => $"{Action} {EntityType} '{EntityName ?? EntityId}' by {UserId ?? "system"} at {Timestamp:yyyy-MM-dd HH:mm:ss}";
}
