# Audit Logging Guide

This document describes the audit logging system for Xpedeon Agent Mission Control, including logging operations, querying logs, and compliance usage.

## Overview

- **Purpose**: Track all CRUD operations for compliance, security, and debugging
- **Scope**: All Create, Update, Delete operations logged automatically
- **Retention**: 90 days (configurable)
- **Storage**: AuditLogs table in database
- **Performance**: Logged asynchronously, non-blocking

## Architecture

```
HTTP Request (Create Agent)
    ↓
Service.CreateAsync(agent)
    ↓
AuditService.LogAsync(
    action: "Create",
    entityType: "Agent",
    entityId: "42",
    beforeSnapshot: null,
    afterSnapshot: { Id: 42, Name: "..." }
)
    ↓
Save to AuditLogs table
    ↓
Log search via UI or API
```

## Audit Log Fields

```csharp
public class AuditLog {
    int Id;                      // Auto-increment ID
    string UserId;               // Who performed the action
    string Action;               // Create, Update, Delete, Execute
    string EntityType;           // Agent, Task, LLMProvider, etc.
    string EntityId;             // The specific entity ID (42)
    string EntityName;           // Human-readable name ("Claude Agent #42")
    string BeforeJson;           // JSON snapshot before change
    string AfterJson;            // JSON snapshot after change
    string ChangeDescription;    // Human-readable diff ("Name: 'Old' → 'New'")
    DateTime Timestamp;          // When action occurred (UTC)
    string IpAddress;            // Client IP for security tracking
    string UserAgent;            // Browser/tool making request
    string RequestPath;          // API endpoint (/api/agents)
    string HttpMethod;           // GET, POST, PUT, DELETE
    int ResponseStatusCode;      // HTTP response status
    string ErrorMessage;         // Error if action failed
    string MetadataJson;         // Custom data per entity
}
```

## Usage

### Logging a CRUD Operation

```csharp
public class AgentService
{
    private readonly AuditService _auditService;

    public async Task<Agent> CreateAgentAsync(CreateAgentRequest request)
    {
        var agent = new Agent
        {
            Name = request.Name,
            Description = request.Description,
            // ...
        };

        await _agentRepository.SaveAsync(agent);

        // Log the creation
        await _auditService.LogAsync(
            action: "Create",
            entityType: "Agent",
            entityId: agent.Id.ToString(),
            entityName: $"{agent.Name} #{agent.Id}",
            afterSnapshot: agent,
            changeDescription: $"Created agent '{agent.Name}'"
        );

        return agent;
    }

    public async Task UpdateAgentAsync(int agentId, UpdateAgentRequest request)
    {
        var beforeSnapshot = await _agentRepository.GetAsync(agentId);

        beforeSnapshot.Name = request.Name;
        beforeSnapshot.Description = request.Description;
        // ... other updates

        await _agentRepository.SaveAsync(beforeSnapshot);

        // Log the update with before/after
        var changeDescription = AuditService.GenerateChangeDescription(
            before: /* original agent */,
            after: beforeSnapshot
        );

        await _auditService.LogAsync(
            action: "Update",
            entityType: "Agent",
            entityId: agentId.ToString(),
            entityName: $"{beforeSnapshot.Name} #{agentId}",
            beforeSnapshot: /* original */,
            afterSnapshot: beforeSnapshot,
            changeDescription: changeDescription
        );
    }

    public async Task DeleteAgentAsync(int agentId)
    {
        var agent = await _agentRepository.GetAsync(agentId);

        await _agentRepository.DeleteAsync(agentId);

        // Log the deletion
        await _auditService.LogAsync(
            action: "Delete",
            entityType: "Agent",
            entityId: agentId.ToString(),
            entityName: $"{agent.Name} #{agentId}",
            beforeSnapshot: agent,
            changeDescription: "Agent deleted"
        );
    }
}
```

### Searching Audit Logs

```csharp
// Get all logs for a user
var userLogs = await _auditService.SearchAsync(
    userId: "john.doe@company.com"
);

// Get all agent creations in the last week
var agentCreations = await _auditService.SearchAsync(
    action: "Create",
    entityType: "Agent",
    fromDate: DateTime.UtcNow.AddDays(-7)
);

// Get all failed operations
var failures = await _auditService.GetFailuresAsync(
    fromDate: DateTime.UtcNow.AddHours(-24)
);

// Get change history for a specific agent
var agentHistory = await _auditService.GetEntityHistoryAsync(
    entityType: "Agent",
    entityId: "42"
);

// Get action summary (Create: 15, Update: 8, Delete: 2)
var summary = await _auditService.GetActionSummaryAsync(
    fromDate: DateTime.UtcNow.AddDays(-30)
);
```

## Log Examples

### Agent Creation

```json
{
  "id": 1001,
  "userId": "admin@company.com",
  "action": "Create",
  "entityType": "Agent",
  "entityId": "42",
  "entityName": "Customer Support Bot #42",
  "beforeJson": null,
  "afterJson": "{\"id\": 42, \"name\": \"Customer Support Bot\", \"description\": \"Handles support tickets\", ...}",
  "changeDescription": "Created agent 'Customer Support Bot'",
  "timestamp": "2026-04-30T14:30:00Z",
  "ipAddress": "192.168.1.100",
  "userAgent": "Mozilla/5.0...",
  "requestPath": "/api/agents",
  "httpMethod": "POST",
  "responseStatusCode": 201,
  "errorMessage": null,
  "metadataJson": null
}
```

### Agent Update

```json
{
  "id": 1002,
  "userId": "admin@company.com",
  "action": "Update",
  "entityType": "Agent",
  "entityId": "42",
  "entityName": "Customer Support Bot #42",
  "beforeJson": "{\"id\": 42, \"name\": \"Customer Support Bot\", \"maxSpawns\": 5, ...}",
  "afterJson": "{\"id\": 42, \"name\": \"Customer Support Bot\", \"maxSpawns\": 10, ...}",
  "changeDescription": "maxSpawns: '5' → '10'; description updated",
  "timestamp": "2026-04-30T15:00:00Z",
  "ipAddress": "192.168.1.100",
  "userAgent": "Mozilla/5.0...",
  "requestPath": "/api/agents/42",
  "httpMethod": "PUT",
  "responseStatusCode": 200,
  "errorMessage": null,
  "metadataJson": null
}
```

### Failed Delete

```json
{
  "id": 1003,
  "userId": "admin@company.com",
  "action": "Delete",
  "entityType": "Agent",
  "entityId": "99",
  "entityName": "Test Agent #99",
  "beforeJson": "{\"id\": 99, ...}",
  "afterJson": null,
  "changeDescription": null,
  "timestamp": "2026-04-30T16:00:00Z",
  "ipAddress": "192.168.1.100",
  "userAgent": "Mozilla/5.0...",
  "requestPath": "/api/agents/99",
  "httpMethod": "DELETE",
  "responseStatusCode": 409,
  "errorMessage": "Cannot delete agent with active tasks",
  "metadataJson": null
}
```

## Retention Policy

### Default: 90 Days

```csharp
// Run during off-peak hours (e.g., 2 AM daily)
var deletedCount = await _auditService.PruneOldLogsAsync(
    retentionDays: 90
);
```

### Compliance

**GDPR Compliance:** Delete user's audit logs upon request
```csharp
// Delete all logs for a user (upon GDPR erasure request)
var userLogs = await _auditService.SearchAsync(userId: "user@example.com");
foreach (var log in userLogs)
{
    await _db.AuditLogs.FindAsync(log.Id).Delete();
}
```

**SOC 2 Compliance:** Retain for 1 year
```csharp
// Keep 365 days for compliance, delete older
var deletedCount = await _auditService.PruneOldLogsAsync(retentionDays: 365);
```

## Querying via UI

### Audit Logs Dashboard

**Search Filters:**
- User ID (e.g., "admin@company.com")
- Action (Create, Update, Delete, Execute)
- Entity Type (Agent, Task, LLMProvider, Skill)
- Date Range (from/to)
- Status (Success/Failure)

**Example Search:** Find all failed deletes in the last 7 days
```
User: (all)
Action: Delete
Status: Failed
From: 2026-04-23
To: 2026-04-30
```

**Results Columns:**
| Time | User | Action | Entity | ID | Status | IP | Details |
|------|------|--------|--------|----|---------|----|---------|
| 2026-04-30 14:30:00 | admin@company.com | Create | Agent | 42 | ✅ | 192.168.1.100 | [View changes] |
| 2026-04-30 15:00:00 | admin@company.com | Update | Agent | 42 | ✅ | 192.168.1.100 | [View changes] |
| 2026-04-30 16:00:00 | admin@company.com | Delete | Agent | 99 | ❌ | 192.168.1.100 | [View error] |

## Security Considerations

### What Gets Logged

✅ **All logged:**
- User ID (who did it)
- Timestamp (when)
- IP address (from where)
- Entity snapshots (what changed)
- Success/failure status

⚠️ **NOT logged (for privacy):**
- Plain-text API keys (already encrypted)
- Passwords (not stored in entities)
- Personal user data (GDPR compliance)

### Access Control

**Read Audit Logs:**
- Admins (unlimited)
- Users (only their own actions)

**Delete Audit Logs:**
- System (automatic pruning)
- Compliance team (upon GDPR request)

## Performance

**Write Performance:**
- Logged asynchronously (non-blocking)
- Typical overhead: <1ms per operation
- No impact on request response time

**Read Performance:**
- Queries indexed on: Timestamp, UserId, Action, EntityType, EntityId
- P95 search: <100ms for 90 days of logs (typical)

**Storage:**
- ~500 bytes per audit log
- 90-day retention: ~45 MB (typical)
- 1 year retention: ~200 MB

## Monitoring

### Audit Log Metrics

```prometheus
xpedeon_audit_logs_total{action="Create"} 1520
xpedeon_audit_logs_total{action="Update"} 3840
xpedeon_audit_logs_total{action="Delete"} 420
xpedeon_audit_logs_failures_total 34
xpedeon_audit_logs_by_user{user="admin@company.com"} 245
```

### Alerts

```yaml
- name: AuditLogFailureSpike
  condition: rate(xpedeon_audit_logs_failures_total[5m]) > 1
  severity: warning

- name: AuditLogDeleteSpike
  condition: rate(xpedeon_audit_logs_total{action="Delete"}[5m]) > 2
  severity: warning
  # Detects unusual deletion patterns
```

## Testing

### Unit Test

```csharp
[Fact]
public async Task LogAsync_CreatesAuditLogEntry()
{
    var agent = new Agent { Id = 1, Name = "Test Agent" };
    
    await _auditService.LogAsync(
        action: "Create",
        entityType: "Agent",
        entityId: "1",
        afterSnapshot: agent
    );

    var logs = await _auditService.SearchAsync(
        entityType: "Agent",
        entityId: "1"
    );

    Assert.Single(logs);
    Assert.Equal("Create", logs[0].Action);
    Assert.Equal("Agent", logs[0].EntityType);
}
```

### Integration Test

```csharp
[Fact]
public async Task AgentService_LogsCreationToAuditTrail()
{
    var request = new CreateAgentRequest { Name = "Test" };
    var agent = await _agentService.CreateAsync(request);

    var logs = await _auditService.GetEntityHistoryAsync(
        "Agent",
        agent.Id.ToString()
    );

    Assert.Single(logs);
    Assert.Contains("Created", logs[0].ChangeDescription);
}
```

## Best Practices

1. **Log Early, Log Often** — Always log CRUD operations
2. **Include Metadata** — Add context-specific data in MetadataJson
3. **Meaningful Descriptions** — Use human-readable change descriptions
4. **Test Log Searches** — Verify admins can find what they need
5. **Review Retention** — Ensure policy matches compliance requirements
6. **Monitor Failures** — Alert on unusual deletion/update patterns
7. **Document Changes** — Reference ticket/issue in MetadataJson

## References

- PRODUCTION_ROADMAP.md - Phase 1.6 requirements
- GDPR audit log requirements
- SOC 2 Type II audit trail requirements
- [Audit Logging Best Practices](https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html)
