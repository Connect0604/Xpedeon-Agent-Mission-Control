# Local Capability Library Design

Date: 2026-04-11
Status: Approved for planning

## Goal

Extend the current local automation agent into a reusable local capability platform that:

- handles common create/read/write/query tasks through built-in capabilities
- detects when a user request cannot be satisfied by the existing capability set
- drafts a new reusable local capability when needed
- requires review and approval before a generated capability becomes active
- saves approved capabilities so later requests reuse them instead of generating fresh PowerShell each time

This design keeps the current same-machine execution model and agent-based workflow integration, but replaces fragile prompt-only behavior with a capability registry plus controlled generation flow.

## Why This Change

The current local automation implementation is good for direct file operations such as copy, move, delete, create folder, and write file. It is weaker for request types like:

- count Excel files in Desktop
- list latest PDF files in Downloads
- read file metadata
- find the newest export matching a pattern

Without first-class capabilities for those tasks, the planner falls back to raw PowerShell planning. That is too brittle for repeated use. The product needs a way to learn reusable local behaviors without requiring a code change for every new query.

## Product Direction

The local automation agent becomes a capability-driven execution agent.

Execution preference:

1. Match the user request to an existing built-in or saved capability.
2. Execute the matched capability with validated inputs.
3. If no capability matches, ask the model to draft a new reusable capability.
4. Validate the draft, pause for approval, then save it if approved.
5. Reuse the saved capability on future tasks.

This allows the system to improve over time while keeping approval, auditing, and path restrictions under Mission Control’s control.

## Capability Types

### Built-In Capabilities

Built-in capabilities are implemented in .NET and shipped with the app. They should cover the most common local tasks.

Initial built-in set:

- `createDirectory`
- `listFiles`
- `countFiles`
- `findFilesByPattern`
- `findLatestFile`
- `getFileMetadata`
- `readTextFile`
- `writeTextFile`
- `appendTextFile`
- `copy`
- `move`
- `rename`
- `zip`
- `unzip`
- `deleteFile`
- `deleteDirectory`

These capabilities are predictable, testable, and preferred over generated PowerShell.

### Generated Capabilities

Generated capabilities are reusable local tools drafted by the model when no built-in or previously saved capability matches the user request.

V1 generated capabilities:

- use PowerShell as the execution language
- are created as drafts first
- require approval before activation
- are saved for later reuse
- remain constrained by agent permissions and allowed roots

Examples:

- `countExcelFiles`
- `listRecentPdfFiles`
- `findLatestReportByPrefix`

## Core Concepts

### Local Capability Registry

Mission Control stores metadata for all local capabilities.

Each capability has:

- `Id`
- `Name`
- `DisplayName`
- `Description`
- `Category` such as `Read`, `Write`, `Query`, `Destructive`
- `ExecutionType` such as `BuiltIn` or `PowerShell`
- `HandlerKey` for built-ins
- `ScriptPath` and optional `ScriptContent` for generated capabilities
- `InputSchemaJson`
- `OutputSchemaJson`
- `AllowedRootsJson`
- `RequiresApproval`
- `IsGenerated`
- `IsActive`
- `Version`
- `CreatedAt`
- `LastUsedAt`
- `SuccessCount`
- `FailureCount`

### Capability Invocation

Tasks no longer need to plan only raw local actions. They can instead produce a structured capability invocation:

```json
{
  "summary": "Count Excel files on Desktop",
  "mode": "invokeCapability",
  "capabilityName": "countFiles",
  "inputs": {
    "rootPath": "%USERPROFILE%\\Desktop",
    "pattern": "*.xls*",
    "recursive": false
  }
}
```

If no capability is available, the planner can return a draft request:

```json
{
  "summary": "Draft reusable capability for counting Excel files",
  "mode": "draftCapability",
  "draft": {
    "name": "countExcelFiles",
    "description": "Count Excel files in a given folder",
    "category": "Query",
    "executionType": "PowerShell",
    "inputs": {
      "rootPath": "string",
      "recursive": "boolean"
    },
    "script": "param([string]$rootPath,[bool]$recursive=$false) ..."
  }
}
```

## Architecture

### New Services

Add a capability layer alongside the existing local automation services.

Primary services:

- `LocalCapabilityService`
  - CRUD for capability records
  - lookup by name, category, or capability fitness
- `LocalCapabilityMatcher`
  - selects the best existing capability for a request
  - decides when to ask for a draft
- `LocalCapabilityExecutor`
  - executes built-in handlers or approved PowerShell-backed capabilities
- `LocalCapabilityDraftValidator`
  - validates generated capability drafts before approval
- `LocalCapabilityScriptStore`
  - persists generated scripts to a controlled project folder

### Existing Services To Evolve

- `LocalAutomationOrchestrator`
  - becomes the top-level local execution coordinator
  - first tries capability invocation
  - falls back to draft generation only when no usable capability exists
- `TaskService`
  - continues to own task lifecycle, approval, persistence, and audit
- `LocalAutomationValidator`
  - remains responsible for allowed-root and permission enforcement on resolved inputs

## Data Flow

### Capability Invocation Flow

1. User submits a task to a local automation-enabled agent.
2. `TaskService` routes to the local automation orchestrator.
3. The orchestrator loads active capabilities from the registry.
4. The model is asked to either:
   - invoke an existing capability, or
   - request a draft when no capability fits.
5. If an existing capability is selected:
   - validate inputs and allowed roots
   - run the capability executor
   - persist outputs, touched paths, and trace on the task

### Capability Draft Flow

1. No suitable capability is found.
2. The model returns a capability draft.
3. Draft validator checks:
   - required metadata is present
   - script exists and is non-empty
   - dangerous patterns are rejected or require approval
   - inputs map to declared schema
4. The task moves to `PendingApproval`.
5. Approval evidence shows:
   - draft name
   - purpose
   - declared inputs
   - generated script
   - requested roots and safety category
6. On approval:
   - script is saved in a controlled folder
   - registry entry is created and activated
   - the original task continues by invoking the new capability

## Storage Design

### Database

Add a new entity such as `LocalCapability`.

Add optional task fields for draft tracking:

- `LocalCapabilityId`
- `LocalCapabilityDraftJson`
- `LocalCapabilityExecutionJson`

These complement the existing local action/task audit fields.

### File Storage

Store generated scripts outside the general source tree in a controlled application folder, for example:

- `LocalCapabilities\Scripts\countExcelFiles.v1.ps1`

The path should be deterministic and versioned so it can be audited and replaced safely.

This is better than saving them as prompt skills because these are executable local capabilities, not guidance text.

## Safety Model

### Approval Rules

Generated capabilities are never auto-activated.

Approval is required for:

- all generated capabilities
- all destructive capabilities
- overwrite behavior
- all PowerShell-backed capabilities in v1

Built-in read/query capabilities may run without approval if the agent policy allows them.

### Allowed Roots

All path-bearing inputs are normalized and checked against allowed roots, including:

- built-in capability inputs
- generated capability inputs
- environment-variable-based paths such as `%USERPROFILE%`

Generated scripts must not bypass root validation. The resolved inputs are validated before execution.

### Execution Boundaries

V1 remains same-machine only.

No generated capability is allowed to:

- write outside allowed roots
- silently elevate privileges
- bypass agent-level PowerShell policy

## UI Changes

### New Capability Library Page

Add a page for browsing and managing local capabilities:

- list active capabilities
- filter by category and execution type
- view capability definition and usage stats
- activate or deactivate a capability

### Task Detail

Enhance task detail to show:

- matched capability name
- capability inputs
- generated draft, when applicable
- script content for approved/generated capabilities
- execution output and touched paths

### Approval Experience

Approval UI should clearly distinguish between:

- approving a risky task execution
- approving a new reusable capability for the system

For generated capabilities, the approval view should explain that approval makes the capability reusable for future tasks.

## Testing Strategy

### Unit Tests

Add tests for:

- capability matching
- built-in capability execution
- draft validation
- allowed-root enforcement on capability inputs
- environment-variable path expansion

### Integration Tests

Add task-flow tests for:

- invoking a built-in capability from a natural-language prompt
- drafting a new capability when no match exists
- approving and saving the capability
- reusing the saved capability on a later task without regenerating it

## Implementation Phases

### Phase 1

Add the built-in capability registry and execution layer for create/read/write/query actions.

Success criteria:

- prompts like "count excel files in Desktop" work through built-in capabilities
- capability execution is persisted on tasks

### Phase 2

Add capability drafting, approval, persistence, and reuse.

Success criteria:

- unknown local tasks can produce reusable capability drafts
- approved drafts are saved and reused on later tasks

### Phase 3

Add capability management UX and telemetry.

Success criteria:

- operator can inspect, disable, and review capability performance

## Recommended Constraints For V1

- Prefer built-in capabilities over generated ones whenever possible.
- Restrict generated capabilities to PowerShell only.
- Require approval before any generated capability becomes active.
- Save generated capabilities in a controlled folder plus DB metadata.
- Keep local automation and capability execution tied to one dedicated local automation agent capability model.

## Decision Summary

This project should evolve from prompt-planned raw local actions into a local capability platform.

The best implementation is a hybrid:

- built-in capabilities for the most common create/read/write/query behaviors
- generated reusable capabilities for gaps
- approval before activation
- persistence for reuse

That gives Mission Control a stable learning path without requiring a code change for every new local automation request.
