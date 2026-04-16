# Local Automation Agent Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a same-machine local automation capability so a dedicated agent can plan and execute validated file-system actions inside normal task runs and workflow steps.

**Architecture:** Extend the existing local `TaskService` path with a new local automation orchestrator that uses the existing LLM layer for planning, validates a structured JSON action plan, executes approved file operations on the host machine, and persists action traces on `AgentTask`. Keep workflow semantics unchanged by reusing the existing `AgentTask` workflow step path.

**Tech Stack:** Blazor Server, .NET 8, EF Core (SQLite/SQL Server), xUnit

---

## File Structure

- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Models\Agent.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Models\AgentTask.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Data\AppDbContext.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Program.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\TaskService.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LLMExecutionService.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Components\Pages\CreateAgent.razor`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Components\Pages\EditAgent.razor`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Components\Pages\AgentDetail.razor`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Models\LocalAutomationModels.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalAutomationValidator.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalAutomationExecutor.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalAutomationOrchestrator.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\XpedeonAgentMissionControl.Tests\LocalAutomationValidatorTests.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\XpedeonAgentMissionControl.Tests\LocalAutomationExecutorTests.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\XpedeonAgentMissionControl.Tests\LocalAutomationTaskFlowTests.cs`

### Task 1: Add the domain model and persistence shape

**Files:**
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\XpedeonAgentMissionControl.Tests\LocalAutomationValidatorTests.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Models\Agent.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Models\AgentTask.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Models\LocalAutomationModels.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Data\AppDbContext.cs`

- [ ] **Step 1: Write the failing validator/domain tests**

```csharp
using XpedeonAgentMissionControl.Models;
using XpedeonAgentMissionControl.Services;

namespace XpedeonAgentMissionControl.Tests;

public class LocalAutomationValidatorTests
{
    [Fact]
    public void Validate_Rejects_PathOutsideAllowedRoots()
    {
        var agent = new Agent
        {
            LocalAutomationEnabled = true,
            AllowedLocalRootsJson = "[\"C:\\\\Allowed\"]"
        };

        var plan = new LocalAutomationPlan
        {
            Summary = "Copy file",
            Actions =
            [
                new LocalAutomationAction
                {
                    Type = LocalAutomationActionType.Copy,
                    Source = "C:\\Blocked\\a.txt",
                    Destination = "C:\\Allowed\\a.txt"
                }
            ]
        };

        var validator = new LocalAutomationValidator();

        var error = Assert.Throws<InvalidOperationException>(() => validator.Validate(agent, plan));

        Assert.Contains("allowed root", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test .\XpedeonAgentMissionControl.Tests\XpedeonAgentMissionControl.Tests.csproj --filter LocalAutomationValidatorTests.Validate_Rejects_PathOutsideAllowedRoots`

Expected: FAIL with missing local automation models or validator types.

- [ ] **Step 3: Add the new model fields and local automation DTOs**

```csharp
public class Agent
{
    public bool LocalAutomationEnabled { get; set; } = false;
    public bool AllowPowerShellScripts { get; set; } = false;
    public bool AllowDestructiveActions { get; set; } = false;
    public string? AllowedLocalRootsJson { get; set; }
}

public class AgentTask
{
    public string? LocalActionPlanJson { get; set; }
    public string? LocalActionResultJson { get; set; }
    public string? TouchedPathsJson { get; set; }
    public bool RequiresElevatedApproval { get; set; }
}

public enum LocalAutomationActionType
{
    CreateDirectory,
    Copy,
    Move,
    Rename,
    Delete,
    WriteTextFile,
    Zip,
    Unzip,
    RunPowerShell
}

public class LocalAutomationPlan
{
    public string Summary { get; set; } = string.Empty;
    public List<LocalAutomationAction> Actions { get; set; } = new();
}

public class LocalAutomationAction
{
    public LocalAutomationActionType Type { get; set; }
    public string? Source { get; set; }
    public string? Destination { get; set; }
    public string? Path { get; set; }
    public string? Content { get; set; }
    public string? Script { get; set; }
    public bool Overwrite { get; set; }
}
```

- [ ] **Step 4: Register persistence mappings in `AppDbContext`**

```csharp
modelBuilder.Entity<Agent>(e =>
{
    e.Property(a => a.AllowedLocalRootsJson);
});

modelBuilder.Entity<AgentTask>(e =>
{
    e.Property(t => t.LocalActionPlanJson);
    e.Property(t => t.LocalActionResultJson);
    e.Property(t => t.TouchedPathsJson);
});
```

- [ ] **Step 5: Run focused tests to verify they pass**

Run: `dotnet test .\XpedeonAgentMissionControl.Tests\XpedeonAgentMissionControl.Tests.csproj --filter LocalAutomationValidatorTests`

Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add Models/Agent.cs Models/AgentTask.cs Models/LocalAutomationModels.cs Data/AppDbContext.cs XpedeonAgentMissionControl.Tests/LocalAutomationValidatorTests.cs
git commit -m "feat: add local automation domain models"
```

### Task 2: Build the validator and file-operation executor

**Files:**
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalAutomationValidator.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalAutomationExecutor.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\XpedeonAgentMissionControl.Tests\LocalAutomationExecutorTests.cs`

- [ ] **Step 1: Write the failing executor tests**

```csharp
using XpedeonAgentMissionControl.Models;
using XpedeonAgentMissionControl.Services;

namespace XpedeonAgentMissionControl.Tests;

public class LocalAutomationExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_Copies_File_Into_Destination()
    {
        var root = Path.Combine(Path.GetTempPath(), $"amc-local-auto-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.txt");
        var dest = Path.Combine(root, "dest.txt");
        await File.WriteAllTextAsync(source, "hello");

        var executor = new LocalAutomationExecutor();
        var result = await executor.ExecuteAsync(new LocalAutomationPlan
        {
            Summary = "copy",
            Actions = [ new LocalAutomationAction { Type = LocalAutomationActionType.Copy, Source = source, Destination = dest } ]
        }, CancellationToken.None);

        Assert.True(File.Exists(dest));
        Assert.Equal("hello", await File.ReadAllTextAsync(dest));
        Assert.True(result.Success);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test .\XpedeonAgentMissionControl.Tests\XpedeonAgentMissionControl.Tests.csproj --filter LocalAutomationExecutorTests.ExecuteAsync_Copies_File_Into_Destination`

Expected: FAIL with missing executor implementation.

- [ ] **Step 3: Implement validation and file execution**

```csharp
public sealed class LocalAutomationValidator
{
    public LocalAutomationPlan Validate(Agent agent, LocalAutomationPlan plan)
    {
        if (!agent.LocalAutomationEnabled)
            throw new InvalidOperationException("Local automation is not enabled for this agent.");

        foreach (var action in plan.Actions)
        {
            if (action.Type == LocalAutomationActionType.RunPowerShell && !agent.AllowPowerShellScripts)
                throw new InvalidOperationException("PowerShell execution is not allowed for this agent.");

            if (action.Type == LocalAutomationActionType.Delete && !agent.AllowDestructiveActions)
                throw new InvalidOperationException("Destructive actions are not allowed for this agent.");
        }

        return plan;
    }
}

public sealed class LocalAutomationExecutor
{
    public async Task<LocalAutomationExecutionResult> ExecuteAsync(LocalAutomationPlan plan, CancellationToken cancellationToken)
    {
        foreach (var action in plan.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (action.Type)
            {
                case LocalAutomationActionType.CreateDirectory:
                    Directory.CreateDirectory(action.Path!);
                    break;
                case LocalAutomationActionType.Copy:
                    File.Copy(action.Source!, action.Destination!, action.Overwrite);
                    break;
                case LocalAutomationActionType.Move:
                    File.Move(action.Source!, action.Destination!, action.Overwrite);
                    break;
                case LocalAutomationActionType.WriteTextFile:
                    await File.WriteAllTextAsync(action.Path!, action.Content ?? string.Empty, cancellationToken);
                    break;
            }
        }

        return new LocalAutomationExecutionResult { Success = true };
    }
}
```

- [ ] **Step 4: Run focused tests to verify they pass**

Run: `dotnet test .\XpedeonAgentMissionControl.Tests\XpedeonAgentMissionControl.Tests.csproj --filter "LocalAutomationValidatorTests|LocalAutomationExecutorTests"`

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add Services/LocalAutomationValidator.cs Services/LocalAutomationExecutor.cs XpedeonAgentMissionControl.Tests/LocalAutomationExecutorTests.cs XpedeonAgentMissionControl.Tests/LocalAutomationValidatorTests.cs
git commit -m "feat: add local automation validation and executor"
```

### Task 3: Route tasks through the local automation orchestrator

**Files:**
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LLMExecutionService.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalAutomationOrchestrator.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\TaskService.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Program.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\XpedeonAgentMissionControl.Tests\LocalAutomationTaskFlowTests.cs`

- [ ] **Step 1: Write the failing task-flow tests**

```csharp
namespace XpedeonAgentMissionControl.Tests;

public class LocalAutomationTaskFlowTests
{
    [Fact]
    public async Task CreateAndRunAsync_Puts_Delete_Plan_Into_PendingApproval()
    {
        // Arrange an agent with LocalAutomationEnabled=true and AllowDestructiveActions=false.
        // Arrange the planner response to emit a delete action.
        // Assert the task transitions to PendingApproval before execution.
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test .\XpedeonAgentMissionControl.Tests\XpedeonAgentMissionControl.Tests.csproj --filter LocalAutomationTaskFlowTests`

Expected: FAIL with missing orchestrator/task routing behavior.

- [ ] **Step 3: Add the orchestrator and wire task routing**

```csharp
public sealed class LocalAutomationOrchestrator
{
    public async Task<LocalAutomationExecutionResult> PlanAndExecuteAsync(
        Agent agent,
        AgentTask task,
        CancellationToken cancellationToken)
    {
        var planningPrompt = $"{task.SystemPromptSnapshot}\n\nReturn only JSON matching the local automation action schema.";
        var llmResult = await _llm.ExecuteAsync(agent, task.Input ?? string.Empty, planningPrompt, cancellationToken, task.Id);
        var plan = JsonSerializer.Deserialize<LocalAutomationPlan>(llmResult.Output)
            ?? throw new InvalidOperationException("Planner did not return a valid local automation plan.");

        task.LocalActionPlanJson = JsonSerializer.Serialize(plan);
        var validated = _validator.Validate(agent, plan);

        if (RequiresApproval(agent, validated))
            return LocalAutomationExecutionResult.PendingApproval(validated);

        return await _executor.ExecuteAsync(validated, cancellationToken);
    }
}

if (agent.LocalAutomationEnabled)
{
    await ExecuteLocalAutomationTaskAsync(taskId, agent, cancellationToken);
    return;
}
```

- [ ] **Step 4: Register the new services**

```csharp
builder.Services.AddScoped<LocalAutomationValidator>();
builder.Services.AddScoped<LocalAutomationExecutor>();
builder.Services.AddScoped<LocalAutomationOrchestrator>();
```

- [ ] **Step 5: Run focused tests to verify they pass**

Run: `dotnet test .\XpedeonAgentMissionControl.Tests\XpedeonAgentMissionControl.Tests.csproj --filter LocalAutomationTaskFlowTests`

Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add Services/LLMExecutionService.cs Services/LocalAutomationOrchestrator.cs Services/TaskService.cs Program.cs XpedeonAgentMissionControl.Tests/LocalAutomationTaskFlowTests.cs
git commit -m "feat: route local automation tasks through orchestrator"
```

### Task 4: Expose local automation controls in the agent UI

**Files:**
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Components\Pages\CreateAgent.razor`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Components\Pages\EditAgent.razor`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Components\Pages\AgentDetail.razor`

- [ ] **Step 1: Add local automation controls to create/edit pages**

```razor
<div class="card card-pad mt-4">
    <h3 class="section-title mb-4">Local Automation</h3>
    <div class="toggle-group">
        <div class="toggle-item">
            <div>
                <div class="toggle-label">Enable Local Automation</div>
                <div class="toggle-desc text-muted text-sm">Allow this agent to execute validated host-machine actions.</div>
            </div>
            <label class="toggle-switch">
                <input type="checkbox" @bind="agent.LocalAutomationEnabled" />
                <span class="toggle-slider"></span>
            </label>
        </div>
        <div class="toggle-item">
            <div>
                <div class="toggle-label">Allow PowerShell</div>
            </div>
            <label class="toggle-switch">
                <input type="checkbox" @bind="agent.AllowPowerShellScripts" />
                <span class="toggle-slider"></span>
            </label>
        </div>
        <div class="toggle-item">
            <div>
                <div class="toggle-label">Allow Destructive Actions</div>
            </div>
            <label class="toggle-switch">
                <input type="checkbox" @bind="agent.AllowDestructiveActions" />
                <span class="toggle-slider"></span>
            </label>
        </div>
    </div>
    <div class="form-group mt-4">
        <label class="form-label">Allowed Root Paths</label>
        <textarea class="form-input font-mono" rows="4" @bind="allowedRootsText"></textarea>
    </div>
</div>
```

- [ ] **Step 2: Show capability status on the agent detail page**

```razor
<div class="config-item">
    <span class="text-muted text-xs">Local Automation</span>
    <span class="text-sm">@(agent.LocalAutomationEnabled ? "Enabled" : "Disabled")</span>
</div>
@if (agent.LocalAutomationEnabled)
{
    <div class="mt-4">
        <div class="text-muted text-xs mb-2">Allowed Roots</div>
        @foreach (var root in AllowedRoots(agent))
        {
            <span class="tag">@root</span>
        }
    </div>
}
```

- [ ] **Step 3: Run build to verify the UI compiles**

Run: `dotnet build .\XpedeonAgentMissionControl.sln`

Expected: BUILD SUCCEEDED

- [ ] **Step 4: Commit**

```bash
git add Components/Pages/CreateAgent.razor Components/Pages/EditAgent.razor Components/Pages/AgentDetail.razor
git commit -m "feat: add local automation agent controls"
```

### Task 5: Persist traces and finish end-to-end verification

**Files:**
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\TaskService.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Components\Pages\AgentDetail.razor`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\XpedeonAgentMissionControl.Tests\LocalAutomationTaskFlowTests.cs`

- [ ] **Step 1: Persist action plan, results, and touched paths on task completion**

```csharp
task.LocalActionPlanJson = JsonSerializer.Serialize(result.Plan);
task.LocalActionResultJson = JsonSerializer.Serialize(result);
task.TouchedPathsJson = JsonSerializer.Serialize(result.TouchedPaths);
task.ExecutionTrace = string.Join(Environment.NewLine, new[]
{
    task.ExecutionTrace,
    result.Trace
}.Where(x => !string.IsNullOrWhiteSpace(x)));
```

- [ ] **Step 2: Surface the persisted trace in task detail**

```razor
@if (!string.IsNullOrWhiteSpace(selectedTask?.LocalActionPlanJson))
{
    <div class="mt-4">
        <div class="text-xs text-muted mb-2">Local Action Plan</div>
        <pre class="code-block">@selectedTask.LocalActionPlanJson</pre>
    </div>
}
```

- [ ] **Step 3: Run the full automated verification**

Run: `dotnet test .\XpedeonAgentMissionControl.Tests\XpedeonAgentMissionControl.Tests.csproj`
Expected: PASS

Run: `dotnet build .\XpedeonAgentMissionControl.sln`
Expected: BUILD SUCCEEDED

- [ ] **Step 4: Commit**

```bash
git add Services/TaskService.cs Components/Pages/AgentDetail.razor XpedeonAgentMissionControl.Tests/LocalAutomationTaskFlowTests.cs
git commit -m "feat: persist and display local automation traces"
```

## Self-Review

- Spec coverage: model changes, validation, execution, approval behavior, UI configuration, workflow reuse, and trace persistence are all covered by Tasks 1-5.
- Placeholder scan: no `TBD`, `TODO`, or deferred implementation markers remain in the plan tasks.
- Type consistency: the plan uses one consistent set of names for the new domain types and services: `LocalAutomationPlan`, `LocalAutomationAction`, `LocalAutomationValidator`, `LocalAutomationExecutor`, and `LocalAutomationOrchestrator`.
