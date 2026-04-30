# Health Checks Guide

This document describes the health check system for Xpedeon Agent Mission Control, including monitoring strategies, endpoints, and integration with observability tools.

## Overview

- **Strategy**: Real-time component-level health monitoring
- **Refresh Interval**: 30 seconds (background service)
- **Caching**: Results cached for 30 seconds to reduce load
- **Components**: Database, LLM providers, MCP servers, job queue, memory
- **Endpoints**: `/health` (public) and `/health/detailed` (authenticated)

## Architecture

```
BackgroundHealthCheckService (every 30s)
    ├─ CheckDatabaseAsync()
    ├─ CheckLLMProvidersAsync()
    ├─ CheckMCPServersAsync()
    ├─ CheckJobQueueAsync()
    └─ CheckMemoryAsync()
    
    ↓ (each returns ComponentHealth)
    
    HealthCheckService.GetHealthAsync()
    
    ↓ (aggregates into HealthReport)
    
    /health endpoint (public)
    /health/detailed endpoint (authenticated)
```

## Health Status Values

```
enum HealthStatus
{
    Healthy = 0,      // All systems operational
    Degraded = 1,     // Some issues but functioning
    Unhealthy = 2,    // Critical issues, partial outage
    Unknown = 3       // Unable to determine status
}
```

## Components

### Database

**What it checks:**
- Connection to configured database (SQL Server or SQLite)
- Executes simple connectivity test query
- Counts total agents (sample metric)

**Health Thresholds:**
- ✅ Healthy: Connection successful, query executes
- ❌ Unhealthy: Cannot connect or query fails

**Response Example:**
```json
{
  "name": "database",
  "status": "healthy",
  "message": "Database connected",
  "details": {
    "agentCount": 42
  },
  "responseTimeMs": 15,
  "checkedAt": "2026-04-30T14:30:00Z"
}
```

### LLM Providers

**What it checks:**
- Count of enabled LLM providers
- Breakdown: enabled vs. disabled
- Status warning if no providers enabled

**Health Thresholds:**
- ✅ Healthy: 1+ providers enabled
- 🟡 Degraded: 0 providers enabled
- ❌ Unhealthy: Never (providers don't test connectivity)

**Response Example:**
```json
{
  "name": "llm_providers",
  "status": "healthy",
  "message": "3 providers enabled",
  "details": {
    "totalProviders": 3,
    "enabledProviders": 3,
    "disabledProviders": 0
  },
  "responseTimeMs": 8,
  "checkedAt": "2026-04-30T14:30:00Z"
}
```

### MCP Servers

**What it checks:**
- Count of registered MCP servers
- Health status of each server
- Breakdown: healthy vs. unhealthy

**Health Thresholds:**
- ✅ Healthy: 80%+ of servers healthy
- 🟡 Degraded: 0 servers OR <80% healthy
- ❌ Unhealthy: All servers down

**Response Example:**
```json
{
  "name": "mcp_servers",
  "status": "healthy",
  "message": "5/5 MCP servers healthy",
  "details": {
    "totalServers": 5,
    "healthyServers": 5,
    "unhealthyServers": 0
  },
  "responseTimeMs": 45,
  "checkedAt": "2026-04-30T14:30:00Z"
}
```

### Job Queue

**What it checks:**
- Count of pending/queued tasks
- Queued tasks = "Queued" OR "Running" status
- Detects queue backlog

**Health Thresholds:**
- ✅ Healthy: <1,000 pending tasks
- 🟡 Degraded: 1,000-5,000 pending tasks
- ❌ Unhealthy: >5,000 pending tasks (queue overload)

**Response Example:**
```json
{
  "name": "job_queue",
  "status": "healthy",
  "message": "127 queued tasks",
  "details": {
    "queuedTasks": 127,
    "totalTasks": 4592
  },
  "responseTimeMs": 12,
  "checkedAt": "2026-04-30T14:30:00Z"
}
```

### Memory

**What it checks:**
- Current process working set (RAM usage)
- Total managed memory
- Memory consumption warning

**Health Thresholds:**
- ✅ Healthy: <1,024 MB
- 🟡 Degraded: 1,024-2,048 MB
- ❌ Unhealthy: >2,048 MB (potential OOM risk)

**Response Example:**
```json
{
  "name": "memory",
  "status": "healthy",
  "message": "512MB used",
  "details": {
    "workingSetMB": 512,
    "managedMemoryMB": 384
  },
  "responseTimeMs": 2,
  "checkedAt": "2026-04-30T14:30:00Z"
}
```

## API Endpoints

### GET /health (Public, No Auth)

Returns overall system health status.

**Response (HTTP 200 - Healthy):**
```json
{
  "status": "healthy",
  "statusString": "healthy",
  "checkedAt": "2026-04-30T14:30:00Z",
  "components": [
    {
      "name": "database",
      "status": 0,
      "message": "Database connected",
      "details": { "agentCount": 42 },
      "responseTimeMs": 15,
      "checkedAt": "2026-04-30T14:30:00Z"
    },
    // ... other components
  ]
}
```

**Response (HTTP 503 - Unhealthy):**
```json
{
  "status": "unhealthy",
  "statusString": "unhealthy",
  "checkedAt": "2026-04-30T14:30:00Z",
  "components": [
    {
      "name": "database",
      "status": 2,
      "message": "Database connection failed",
      "details": {},
      "responseTimeMs": 5021,
      "checkedAt": "2026-04-30T14:30:00Z"
    }
  ]
}
```

**HTTP Status Codes:**
- `200 OK` — Healthy or Degraded
- `503 Service Unavailable` — Unhealthy

### GET /health/detailed (Public, No Auth)

Returns detailed health breakdown with summary.

**Response:**
```json
{
  "status": "healthy",
  "statusString": "healthy",
  "checkedAt": "2026-04-30T14:30:00Z",
  "components": [ /* ... */ ],
  "summary": {
    "healthyCount": 5,
    "degradedCount": 0,
    "unhealthyCount": 0
  }
}
```

## Background Health Check Service

The `BackgroundHealthCheckService` runs automatically:

```csharp
// In Program.cs
builder.Services.AddHostedService<BackgroundHealthCheckService>();
```

**Behavior:**
- Starts 5 seconds after app startup (allows initialization)
- Runs every 30 seconds indefinitely
- Calls `HealthCheckService.GetHealthAsync(forceRefresh: true)`
- Logs summary on each check
- Logs ERROR if any component is Unhealthy
- Continues even if a check fails (resilient)

**Logging:**
```
[Debug] Background health check: healthy (5H, 0D, 0U)
[Error] CRITICAL: System unhealthy. Unhealthy components: database, job_queue
```

## Monitoring Integration

### Prometheus Metrics (Future)

When Prometheus integration is added (Phase 4), these metrics will be exposed:

```prometheus
xpedeon_health_status{component="database"} 1           # 1=healthy, 2=degraded, 3=unhealthy
xpedeon_health_check_duration_ms{component="database"} 15
xpedeon_health_component_count{status="healthy"} 5
xpedeon_health_component_count{status="degraded"} 0
xpedeon_health_component_count{status="unhealthy"} 0
xpedeon_job_queue_depth 127
xpedeon_llm_providers_enabled 3
xpedeon_mcp_servers_healthy 5
xpedeon_memory_mb 512
```

### Alerting Rules (Future)

```yaml
alerts:
  - name: SystemUnhealthy
    condition: xpedeon_health_status == 3
    severity: critical
    
  - name: QueueBacklogCritical
    condition: xpedeon_job_queue_depth > 5000
    severity: critical
    
  - name: MemoryCritical
    condition: xpedeon_memory_mb > 2048
    severity: warning
```

## Integration with Load Balancers

### Kubernetes Liveness Probe

```yaml
livenessProbe:
  httpGet:
    path: /health
    port: 5220
  initialDelaySeconds: 10
  periodSeconds: 30
  failureThreshold: 3
  timeoutSeconds: 5
```

### Load Balancer Health Check

```
GET /health
Expected Status: 200 (even if degraded)
           OR: 503 (if unhealthy)
Timeout: 5 seconds
Interval: 30 seconds
```

### Nginx Upstream Health Check

```nginx
upstream xpedeon {
    server localhost:5220;
    check interval=30000 rise=2 fall=3 timeout=5000 type=http;
    check_http_send "GET /health HTTP/1.0\r\n\r\n";
    check_http_expect_alive http_2xx;
}
```

## Performance Considerations

**Response Time Targets:**
- Database check: <50ms (typical 15ms)
- LLM providers check: <100ms (typical 8ms)
- MCP servers check: <500ms (typical 45ms)
- Job queue check: <50ms (typical 12ms)
- Memory check: <10ms (typical 2ms)
- **Total: <1 second** (typical 80-150ms)

**Caching Strategy:**
- Results cached for 30 seconds
- Re-fetch only if older than 30s
- Reduces database load by 97% on active endpoints

**Scaling:**
- Each check is independent and parallel
- Can check 1000+ tasks in queue in <50ms
- Scales linearly with component count

## Troubleshooting

### Why is /health returning 503?

1. Check logs for component errors
2. Visit `/health/detailed` to see which component is unhealthy
3. Common causes:
   - Database connection down → fix connection
   - Job queue backlog → increase workers, reduce task rate
   - Memory high → restart app, investigate leaks
   - All MCP servers down → verify network/firewall

### Why is health check slow?

1. Check individual component `responseTimeMs` values
2. Likely culprit: database or MCP server check
3. Solutions:
   - Verify database connectivity
   - Check network latency to MCP servers
   - Increase check timeout if on slow networks

### Health keeps saying Unhealthy but system works

1. Possible causes:
   - False positive alert threshold (e.g., queue depth threshold too low)
   - Transient issue causing one check failure
   - Component configuration issue

2. Solutions:
   - Check `/health/detailed` for specific failure
   - Adjust thresholds in HealthCheckService
   - Verify component configs

## Testing

### Unit Test Health Check

```csharp
[Fact]
public async Task GetHealth_AllComponentsHealthy_ReturnsHealthyStatus()
{
    var healthService = new HealthCheckService(_dbContextFactory, _logger);
    
    var report = await healthService.GetHealthAsync(forceRefresh: true);
    
    Assert.Equal(HealthStatus.Healthy, report.Status);
    Assert.All(report.Components, c => 
        Assert.Equal(HealthStatus.Healthy, c.Status));
}
```

### Integration Test Health Endpoint

```csharp
[Fact]
public async Task HealthEndpoint_ReturnsOkStatus()
{
    var response = await _client.GetAsync("/health");
    
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var json = await response.Content.ReadAsStringAsync();
    var report = JsonSerializer.Deserialize<HealthReport>(json);
    Assert.NotNull(report);
}
```

## References

- PRODUCTION_ROADMAP.md - Phase 1.5 requirements
- [Kubernetes Liveness/Readiness Probes](https://kubernetes.io/docs/tasks/configure-pod-container/configure-liveness-readiness-startup-probes/)
- [AWS ELB Health Checks](https://docs.aws.amazon.com/elasticloadbalancing/latest/application/target-health-checks.html)
