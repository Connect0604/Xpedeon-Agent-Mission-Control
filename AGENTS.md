# AGENTS.md

This file provides guidance to Codex (Codex.ai/code) when working with code in this repository.

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
```

No test projects exist in this solution.

## Architecture Overview

Blazor Server (.NET 8) multi-agent orchestration dashboard — "mission control" for managing AI agents running against an enterprise ERP (Xpedeon).

### Data Layer
- **SQLite** via EF Core with `IDbContextFactory<AppDbContext>` (scoped per operation)
- **Dev mode resets the DB on every startup** (`EnsureDeleted()` + `EnsureCreated()` in `Program.cs`)
- DB file: `{ContentRoot}/xpedeon.db`
- Migrations are not used — schema is always recreated from models

### Service Registration (`Program.cs`)
All services are DI-registered. Key ones:
- `AgentService` — CRUD + metrics; prompt versioning on every update
- `TaskService` — task lifecycle, approval gating, async execution via `Task.Run()`
- `LLMExecutionService` — routes to Codex / OpenAI / Ollama / Custom endpoints; tracks tokens + USD cost
- `DynamicSpawnService` — decomposes tasks into sub-tasks, spawns child agents at runtime
- `SwarmService` — multi-agent swarm coordination strategies
- `MockDataService` (Singleton) — simulation feed; seeds UI before real agents/tasks exist; ticks every 2.5s
- `AgentSchedulerService` — Quartz.NET hosted service for scheduled task execution
- `RealtimeService` — wraps SignalR hub for push updates

### Task Execution Flow
```
TaskService.CreateAndRunAsync()
  → [approval gate: RequiresApproval → PendingApproval status, else continue]
  → Task.Run(async):
      if agent.SpawnEnabled:
        DynamicSpawnService.ExecuteWithSpawningAsync()
          → decompose input (rule-based or LLM-decided)
          → spawn ephemeral child agents per sub-task
          → execute in parallel, wait with timeout
          → aggregate: CollectAll | FirstWins | Voting | LLMSynthesize
      else:
        LLMExecutionService.ExecuteAsync(agent, input, prompt)
          → Codex / OpenAI / Ollama / Custom
  → Update task output, agent metrics (tokens, cost, success rate)
  → Log + notify via SignalR
```

### Dynamic Agent Spawning
Agents can be configured to decompose their input at runtime. Two modes:
- **Rule-based**: splits by newline, comma, or JSON array
- **LLM-decided**: uses Codex as orchestrator to decompose intelligently

Child agents can be **ephemeral** (deleted after task completes) or **persistent**. Max spawns, max depth, and per-child timeout are configurable per agent.

### LLM Providers
`LLMExecutionService` abstracts four provider types (`Codex`, `OpenAI`, `Ollama`, `Custom`). Providers are stored in the DB and configurable at runtime via the Providers page. API keys/endpoints live on the `LLMProvider` model — not in `appsettings.json`.

### Real-Time UI
- SignalR hub at `/hubs/agent` (`AgentHub`)
- UI pages poll every 3 seconds + receive SignalR push events
- `MockDataService` fires events to trigger refresh when simulation data changes

### Key Models
- `Agent` — includes spawn config, circuit breaker state, memory config, prompt history, parent/child hierarchy
- `AgentTask` — tracks tokens, USD cost, confidence, user feedback, spawn depth
- `LLMProvider` — per-provider endpoint, API key, model, temperature
- `Swarm` — grouping of agents with a coordination strategy
- `AgentTemplate` — pre-built templates; 7 Xpedeon-specific templates are seeded at startup

### CSS Design System
Custom dark theme via CSS variables in `wwwroot/app.css`. Status colors: `--c-active`, `--c-warning`, `--c-error`, `--c-idle`. Uses Bootstrap Icons (CDN) and Inter/JetBrains Mono fonts.
