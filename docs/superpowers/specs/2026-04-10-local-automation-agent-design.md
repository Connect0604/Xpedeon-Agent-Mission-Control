# Local Automation Agent Design

## Goal

Add an OpenClaw-inspired local automation capability to Xpedeon Agent Mission Control so a single reusable agent can perform host-machine actions such as copying files, moving files, creating folders, and running approved PowerShell commands as part of normal task runs and workflow steps.

## Problem Statement

Today Mission Control can orchestrate agents, workflows, swarms, MCP servers, and Hermes/OpenClaw delegated runs, but local execution inside Mission Control is limited to LLM task processing and internal orchestration. The product cannot yet let an agent do practical machine work on the Windows host, which makes common ERP automation scenarios awkward:

- move generated reports into distribution folders
- rename or archive exports after processing
- create drop folders for downstream systems
- zip or unzip files before handoff
- run local scripts that bridge ERP output with surrounding tooling

The new capability should make one agent act as a host-local operator while preserving Mission Control's existing strengths around approval, task history, event timelines, and workflow reuse.

## Scope

### In Scope

- Same-machine automation only
- One or more agents can be configured with local automation capability, but the intended operating model is a single reusable automation agent
- Structured local action execution within the existing `TaskService` lifecycle
- Workflow compatibility without introducing a new workflow step type
- Approval-aware execution for destructive or high-risk actions
- Full task trace and event logging for executed actions
- Agent-level capability configuration in create/edit UI

### Out of Scope

- Remote runners or automation on other machines
- Browser/UI automation
- Full unrestricted shell access by default
- File watchers or event-triggered host automation
- New chat/channel integrations
- A plugin platform for arbitrary host extensions

## User Experience

### Agent Setup

Agent creation and editing gain a new "Local Automation" configuration section. Operators can:

- enable or disable local automation capability
- allow or block PowerShell execution
- allow or block destructive actions such as delete and overwrite move behavior
- configure a newline-separated set of allowed root paths

This capability is intended for a dedicated automation agent that can then be used in workflow steps like any other agent.

### Task Execution

When a task is sent to a local-automation-capable agent:

1. The existing LLM execution path is still used for planning.
2. The system prompt instructs the model to return a structured local action plan.
3. Mission Control validates the action plan before touching the host machine.
4. If the plan contains risky actions, the task is routed to approval before execution.
5. The local executor runs each action and records results in the task output and execution trace.

### Workflow Execution

No workflow model change is required. A workflow step targeting the automation agent behaves like any other `AgentTask` step:

- upstream steps produce instructions or file paths
- the automation agent executes validated local actions
- downstream steps can consume the resulting task output

## Recommended Technical Approach

Implement a first-class local automation executor inside Mission Control rather than expressing host automation through MCP or raw unrestricted shell calls.

### Why This Approach

- It fits the existing `TaskService` execution model cleanly.
- It keeps approval, audit, and workflow behavior centralized.
- It avoids coupling core product value to external MCP server definitions.
- It is safer than direct arbitrary shell execution while still enabling practical operations.

## Architecture

### Core Flow

```text
TaskService.CreateAndRunAsync
  -> task created / approval evidence built
  -> ExecuteTaskAsync
      -> if agent has LocalAutomationEnabled
          -> LocalAutomationOrchestrator.PlanAndExecuteAsync
              -> LLMExecutionService builds structured action plan
              -> LocalAutomationValidator validates plan
              -> if risky and not yet approved -> pending approval
              -> LocalAutomationExecutor performs actions
              -> task output + trace + events updated
      -> else existing LLM-only execution path
```

### New Components

#### `LocalAutomationOrchestrator`

Coordinates planning, validation, approval escalation, execution, and trace formatting.

Responsibilities:

- invoke the LLM with a local automation action contract
- parse structured response into typed action objects
- validate all actions before execution
- determine whether the task must be upgraded to approval-gated execution
- execute actions sequentially
- aggregate execution results into task output and trace

#### `LocalAutomationValidator`

Validates action plans before any host action is executed.

Responsibilities:

- ensure action types are supported
- verify required arguments are present
- normalize and validate paths
- ensure every touched path is under an allowed root when roots are configured
- block destructive actions when the agent is not configured for them
- block PowerShell actions when the agent is not configured for them

#### `LocalAutomationExecutor`

Runs validated actions on the host machine.

Responsibilities:

- perform file and directory operations using .NET APIs
- optionally execute PowerShell via `System.Diagnostics.Process`
- capture action-level status, error message, stdout, stderr, and affected paths

#### `LocalAutomationAction` model family

Represents typed actions and results.

Example actions:

- `CopyFile`
- `MovePath`
- `DeletePath`
- `CreateDirectory`
- `RenamePath`
- `WriteTextFile`
- `ZipPath`
- `UnzipPath`
- `RunPowerShell`

## Data Model Changes

### `Agent`

Add:

- `LocalAutomationEnabled : bool`
- `AllowPowerShellScripts : bool`
- `AllowDestructiveActions : bool`
- `AllowedLocalRootsJson : string?`

These fields make capability and safety constraints explicit and agent-scoped.

### `AgentTask`

Add:

- `LocalActionPlanJson : string?`
- `LocalActionResultJson : string?`
- `TouchedPathsJson : string?`
- `RequiresElevatedApproval : bool`

These fields persist the action plan, execution result, and audited path set for display and replay.

## Action Contract

The LLM should return a structured JSON payload instead of prose when local automation is enabled.

### Shape

```json
{
  "summary": "Copy the generated report into the archive folder and zip it.",
  "actions": [
    {
      "type": "copy",
      "source": "C:\\Reports\\Daily\\report.xlsx",
      "destination": "C:\\Exports\\Archive\\report.xlsx",
      "overwrite": true
    },
    {
      "type": "zip",
      "source": "C:\\Exports\\Archive\\report.xlsx",
      "destination": "C:\\Exports\\Archive\\report.zip",
      "overwrite": true
    }
  ]
}
```

### Important Constraints

- The LLM is a planner, not the direct executor.
- Validation happens before execution.
- Free-form shell output is not trusted as an execution mechanism.

## Supported V1 Actions

### Safe-by-default actions

- create directory
- copy file
- copy directory
- move file or directory
- rename file or directory
- write text file
- zip
- unzip

### Gated actions

- delete file or directory
- overwrite operations if destructive policy is disabled
- PowerShell execution

## Approval Rules

Mission Control already has a task approval mechanism. The new feature extends approval behavior instead of creating a new approval system.

### Approval triggers

Task execution should require approval when any of the following are true:

- the agent already has `RequiresApproval`
- the action plan contains `delete`
- the action plan contains `run-powershell`
- the action plan attempts to overwrite existing paths and destructive actions are configured as gated
- validation identifies an action requiring elevated review

### Approval evidence

Approval evidence should include:

- action summary
- list of intended actions
- list of touched paths
- whether PowerShell will be used
- whether destructive behavior is requested

## Error Handling

### Validation failures

If the plan is malformed or violates policy:

- mark the task as failed
- store a clear validation error in `ErrorMessage`
- log a task execution event describing the blocked action

### Execution failures

Execution is sequential. If an action fails:

- stop remaining actions
- mark the task failed
- record which action failed and why
- preserve results for completed prior actions

### Cancellation

Longer-running actions, especially PowerShell, must honor cancellation where practical:

- kill spawned PowerShell process on cancellation
- mark the task cancelled
- persist partial execution results

## UI Changes

### `CreateAgent.razor`

Add local automation configuration controls in the safety/capability area.

### `EditAgent.razor`

Expose the same controls for existing agents.

### `AgentDetail.razor`

Show:

- whether local automation is enabled
- allowed roots
- PowerShell and destructive-action settings
- action plan and execution result details in task inspection views

## Testing Strategy

Add automated coverage despite the repository not having traditional test projects in active use. There is already an `XpedeonAgentMissionControl.Tests` folder present, which should be used for this feature.

### Unit tests

- path normalization and allowed-root validation
- destructive action gating
- PowerShell gating
- JSON action plan parsing

### Service tests

- task routing to local automation path
- approval escalation on risky plans
- successful copy/move/create-directory execution against temporary folders
- failure propagation when a source path does not exist

## Phased Delivery

### Phase 1

- agent model fields
- action models
- validator
- executor for file operations
- task trace persistence
- create/edit UI

### Phase 2

- approval enrichment
- task detail rendering for local action traces
- workflow-focused polish

### Phase 3

- gated PowerShell execution
- richer result formatting
- replay improvements

## Risks and Mitigations

### Risk: unrestricted host access

Mitigation:

- capability is opt-in per agent
- allowed roots constrain file system scope
- risky actions require approval

### Risk: fragile LLM output formatting

Mitigation:

- enforce strict JSON contract
- validate before execution
- keep action vocabulary small in v1

### Risk: accidental destructive operations

Mitigation:

- destructive actions are explicitly configured
- overwrite and delete are logged and approval-gated

### Risk: workflow confusion

Mitigation:

- keep workflow semantics unchanged
- reuse the normal agent step model rather than inventing a second automation system

## Success Criteria

The feature is successful when:

- an operator can configure one dedicated automation agent
- that agent can be dropped into an existing workflow
- the agent can successfully perform basic file operations on the Mission Control host
- risky plans are approval-gated
- every executed action is visible in task history and trace

## Recommendation

Implement the local automation feature as a first-class Mission Control capability for local agents, using the LLM as a planner and a typed local executor as the enforcement boundary. This best matches the current architecture, solves the user's requested problem directly, and creates a strong foundation for later expansion to event triggers or remote runners without overcommitting in v1.
