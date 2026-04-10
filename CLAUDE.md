# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Restore dependencies
dotnet restore

# Build
dotnet build

# Run (dev, HTTPS on https://localhost:7220)
dotnet run

# Run (HTTP on http://localhost:5220)
dotnet run --launch-profile http

# Run all tests
dotnet test XpedeonAgentMissionControl.Tests

# Run a specific test by name
dotnet test XpedeonAgentMissionControl.Tests --filter "FullyQualifiedName~TestMethodName"
```

## Configuration

**`database.config.json`** (required, loaded explicitly in `Program.cs`) configures the DB provider:
- `"Provider": "SQLite"` — uses `FilePath` (default `xpedeon.db` in content root)
- `"Provider": "SqlServer"` — builds a connection string from `Server`, `Database`, `UserId`, `Password`, etc.

`appsettings.json` is not used for DB or LLM config. LLM provider API keys/endpoints are stored in the DB itself (via the Providers page at runtime).

**`HermesOpenClaw`** section (in `appsettings.json`) configures the optional external execution backend — set `Enabled: true` with `BaseUrl` and `ApiKey` to route agents through it.

## Architecture Overview

Blazor Server (.NET 8) multi-agent orchestration dashboard — "mission control" for managing AI agents running against an enterprise ERP (Xpedeon).

### Data Layer
- **SQLite or SQL Server** via EF Core with `IDbContextFactory<AppDbContext>` (scoped per operation)
- Migrations are not used — schema is recreated from models via `EnsureCreated()`
- `AppDbContext` takes a `DatabaseConfig` dependency (injected as singleton) to know which schema to use on SQL Server

### Service Registration (`Program.cs`)
All services are DI-registered. Key ones:
- `AgentService` — CRUD + metrics; prompt versioning on every update
- `TaskService` — task lifecycle, approval gating, async execution via `Task.Run()`
- `LLMExecutionService` — routes to Claude / OpenAI / Ollama / Custom endpoints; tracks tokens + USD cost
- `DynamicSpawnService` — decomposes tasks into sub-tasks, spawns child agents at runtime
- `SwarmService` — multi-agent swarm coordination strategies
- `MCPService` — MCP server registry and connection testing; also implements `IMcpConnectionProbe` (registered separately for testability)
- `ExternalSkillPackageService` — imports, approves, and provisions skill packages from GitHub
- `HermesOpenClawExecutionService` — submits tasks to the external Hermes/OpenClaw execution platform
- `HermesOpenClawSyncService` (hosted) — polls HermesOpenClaw for status updates on submitted runs
- `MockDataService` (Singleton) — simulation feed; seeds UI before real agents/tasks exist; ticks every 2.5s
- `AgentSchedulerService` (hosted) — Quartz.NET scheduled task execution
- `RealtimeService` — wraps SignalR hub for push updates

### Task Execution Flow
```
TaskService.CreateAndRunAsync()
  → [approval gate: RequiresApproval → PendingApproval status, else continue]
  → Task.Run(async):
      if agent.ExecutionBackend == HermesOpenClaw:
        HermesOpenClawExecutionService.SubmitTaskAsync()
          → POST to /api/runs; HermesOpenClawSyncService polls for completion
      else if agent.SpawnEnabled:
        DynamicSpawnService.ExecuteWithSpawningAsync()
          → decompose input (rule-based or LLM-decided)
          → spawn ephemeral child agents per sub-task
          → execute in parallel, wait with timeout
          → aggregate: CollectAll | FirstWins | Voting | LLMSynthesize
      else:
        LLMExecutionService.ExecuteAsync(agent, input, prompt)
          → Claude / OpenAI / Ollama / Custom
  → Update task output, agent metrics (tokens, cost, success rate)
  → Log + notify via SignalR
```

### External Skill Packages
`ExternalSkillPackageService` manages a lifecycle for skill packages imported from GitHub URLs:

1. **Import** — fetches manifest from raw GitHub URL; detects kind automatically:
   - `ManagedMcp`: JSON manifest (`xpedeon-skill.json`); declares tools, runtime endpoint, and default skills
   - `AgentSkill`: Markdown file (`SKILL.md`) with YAML frontmatter; creates `SkillDefinition` records directly, no MCP server needed
2. **Approve** — moves to `Approved`; for `AgentSkill` packages, immediately moves to `Healthy` and enables linked skills
3. **Provision** (`ManagedMcp` only) — creates a `MCPServer` record pointing at the declared runtime endpoint
4. **Health check** — connects via `IMcpConnectionProbe`, verifies discovered tool names match declared tools; enables linked `SkillDefinition` records on success

`IMcpConnectionProbe` is an interface over `MCPService` — injected into `ExternalSkillPackageService` to allow fake probes in tests.

### Dynamic Agent Spawning
Agents can be configured to decompose their input at runtime. Two modes:
- **Rule-based**: splits by newline, comma, or JSON array
- **LLM-decided**: uses Claude as orchestrator to decompose intelligently

Child agents can be **ephemeral** (deleted after task completes) or **persistent**. Max spawns, max depth, and per-child timeout are configurable per agent.

### LLM Providers
`LLMExecutionService` abstracts four provider types (`Claude`, `OpenAI`, `Ollama`, `Custom`). Providers are stored in the DB and configurable at runtime via the Providers page.

### Real-Time UI
- SignalR hub at `/hubs/agent` (`AgentHub`)
- UI pages poll every 3 seconds + receive SignalR push events
- `MockDataService` fires events to trigger refresh when simulation data changes

### Key Models
- `Agent` — includes spawn config, circuit breaker state, memory config, prompt history, parent/child hierarchy, `ExecutionBackend` (Local vs HermesOpenClaw)
- `AgentTask` — tracks tokens, USD cost, confidence, user feedback, spawn depth
- `LLMProvider` — per-provider endpoint, API key, model, temperature
- `Swarm` — grouping of agents with a coordination strategy
- `MCPServer` — registered MCP server; transport types: SSE, Stdio, WebSocket, Http; auth: None, BearerToken, MicrosoftDeviceCode
- `SkillDefinition` — reusable skill with a prompt snippet and optional MCP tool allow-list; linked to agents via `AgentSkillAssignment`
- `ExternalSkillPackage` / `ExternalSkillPackageInstall` — tracks import/approval/provisioning lifecycle for externally-sourced skills
- `AgentTemplate` — pre-built templates; 7 Xpedeon-specific templates are seeded at startup

### Tests
xUnit project at `XpedeonAgentMissionControl.Tests/`. Tests use an in-memory SQLite connection (not the app's real DB) via a `TestHarness` helper that creates an isolated `AppDbContext`. HTTP calls are stubbed via `IHttpClientFactory`. MCP probing is stubbed via `IMcpConnectionProbe`. Build artifacts go to `artifacts/tests-*` to stay separate from the app build.

### CSS Design System
Custom dark theme via CSS variables in `wwwroot/app.css`. Status colors: `--c-active`, `--c-warning`, `--c-error`, `--c-idle`. Uses Bootstrap Icons (CDN) and Inter/JetBrains Mono fonts.
