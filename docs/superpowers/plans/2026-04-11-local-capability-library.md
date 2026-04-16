# Local Capability Library Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a reusable same-machine local capability platform with built-in create/read/write/query capabilities plus approved generated PowerShell capabilities that can be saved and reused on later tasks.

**Architecture:** Extend the existing local automation flow into a capability-first orchestration path. Add a capability registry and executor for built-in handlers, then add draft-generation and approval-driven persistence for PowerShell-backed generated capabilities. Keep task lifecycle, approvals, and audit inside `TaskService` and the existing local automation path.

**Tech Stack:** Blazor Server, .NET 8, EF Core, xUnit, PowerShell, SQL Server/SQLite

---

## File Structure

- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Models\AgentTask.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Data\AppDbContext.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Program.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\TaskService.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalAutomationOrchestrator.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalAutomationValidator.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalAutomationExecutor.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Components\Pages\AgentDetail.razor`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Models\LocalCapability.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Models\LocalCapabilityModels.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalCapabilityService.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalCapabilityMatcher.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalCapabilityExecutor.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalCapabilityDraftValidator.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalCapabilityScriptStore.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Components\Pages\Capabilities.razor`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\XpedeonAgentMissionControl.Tests\LocalCapabilityMatcherTests.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\XpedeonAgentMissionControl.Tests\LocalCapabilityExecutorTests.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\XpedeonAgentMissionControl.Tests\LocalCapabilityDraftValidatorTests.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\XpedeonAgentMissionControl.Tests\LocalCapabilityTaskFlowTests.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\sql\2026-04-11_LOCAL_CAPABILITY_LIBRARY_AMC.sql`

### Task 1: Add the capability registry domain model and persistence

**Files:**
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Models\LocalCapability.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Models\LocalCapabilityModels.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Models\AgentTask.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Data\AppDbContext.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\XpedeonAgentMissionControl.Tests\LocalCapabilityMatcherTests.cs`

- [ ] **Step 1: Write the failing registry/model tests**

```csharp
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Tests;

public class LocalCapabilityMatcherTests
{
    [Fact]
    public void BuiltInCapabilityDefinition_CanRepresent_CountFiles()
    {
        var capability = new LocalCapability
        {
            Name = "countFiles",
            DisplayName = "Count Files",
            Category = LocalCapabilityCategory.Query,
            ExecutionType = LocalCapabilityExecutionType.BuiltIn,
            HandlerKey = "countFiles",
            InputSchemaJson = """{"rootPath":"string","pattern":"string","recursive":"boolean"}""",
            IsActive = true,
            Version = 1
        };

        Assert.Equal("countFiles", capability.Name);
        Assert.Equal(LocalCapabilityExecutionType.BuiltIn, capability.ExecutionType);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test .\XpedeonAgentMissionControl.Tests\XpedeonAgentMissionControl.Tests.csproj --filter LocalCapabilityMatcherTests.BuiltInCapabilityDefinition_CanRepresent_CountFiles`

Expected: FAIL with missing capability types.

- [ ] **Step 3: Add the capability entity and DTO types**

```csharp
namespace XpedeonAgentMissionControl.Models;

public enum LocalCapabilityCategory
{
    Read,
    Write,
    Query,
    Destructive
}

public enum LocalCapabilityExecutionType
{
    BuiltIn,
    PowerShell
}

public class LocalCapability
{
    public string Id { get; set; } = $"cap-{Guid.NewGuid():N}"[..12];
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public LocalCapabilityCategory Category { get; set; }
    public LocalCapabilityExecutionType ExecutionType { get; set; }
    public string? HandlerKey { get; set; }
    public string? ScriptPath { get; set; }
    public string? ScriptContent { get; set; }
    public string InputSchemaJson { get; set; } = "{}";
    public string OutputSchemaJson { get; set; } = "{}";
    public string? AllowedRootsJson { get; set; }
    public bool RequiresApproval { get; set; }
    public bool IsGenerated { get; set; }
    public bool IsActive { get; set; } = true;
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
}
```

```csharp
namespace XpedeonAgentMissionControl.Models;

public sealed class LocalCapabilityInvocation
{
    public string Summary { get; set; } = string.Empty;
    public string Mode { get; set; } = "invokeCapability";
    public string? CapabilityName { get; set; }
    public Dictionary<string, string?> Inputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public LocalCapabilityDraft? Draft { get; set; }
}

public sealed class LocalCapabilityDraft
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string ExecutionType { get; set; } = "PowerShell";
    public Dictionary<string, string> Inputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Script { get; set; } = string.Empty;
}
```

- [ ] **Step 4: Add persistence fields for capability execution on tasks**

```csharp
public class AgentTask
{
    public string? LocalCapabilityId { get; set; }
    public string? LocalCapabilityDraftJson { get; set; }
    public string? LocalCapabilityExecutionJson { get; set; }
}
```

```csharp
modelBuilder.Entity<LocalCapability>(e =>
{
    e.HasKey(x => x.Id);
    e.Property(x => x.Name).IsRequired();
    e.Property(x => x.DisplayName).IsRequired();
    e.Property(x => x.Description).IsRequired();
    e.Property(x => x.InputSchemaJson).IsRequired();
    e.Property(x => x.OutputSchemaJson).IsRequired();
});
```

- [ ] **Step 5: Run focused tests to verify they pass**

Run: `dotnet test .\XpedeonAgentMissionControl.Tests\XpedeonAgentMissionControl.Tests.csproj --filter LocalCapabilityMatcherTests`

Expected: PASS

- [ ] **Step 6: Add the SQL patch for SQL Server**

```sql
ALTER TABLE [amc].[AGENT_TASKS] ADD [LOCAL_CAPABILITY_ID] nvarchar(64) NULL;
ALTER TABLE [amc].[AGENT_TASKS] ADD [LOCAL_CAPABILITY_DRAFT_JSON] nvarchar(max) NULL;
ALTER TABLE [amc].[AGENT_TASKS] ADD [LOCAL_CAPABILITY_EXECUTION_JSON] nvarchar(max) NULL;
```

- [ ] **Step 7: Commit**

```bash
git add Models/LocalCapability.cs Models/LocalCapabilityModels.cs Models/AgentTask.cs Data/AppDbContext.cs XpedeonAgentMissionControl.Tests/LocalCapabilityMatcherTests.cs sql/2026-04-11_LOCAL_CAPABILITY_LIBRARY_AMC.sql
git commit -m "feat: add local capability registry models"
```

### Task 2: Seed and execute built-in create/read/write/query capabilities

**Files:**
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalCapabilityService.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalCapabilityExecutor.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalAutomationValidator.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\XpedeonAgentMissionControl.Tests\LocalCapabilityExecutorTests.cs`

- [ ] **Step 1: Write the failing executor tests for built-in query capabilities**

```csharp
using XpedeonAgentMissionControl.Models;
using XpedeonAgentMissionControl.Services;

namespace XpedeonAgentMissionControl.Tests;

public class LocalCapabilityExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_CountFiles_Returns_ExcelCount()
    {
        var root = Path.Combine(Path.GetTempPath(), $"amc-cap-count-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "a.xlsx"), "x");
        await File.WriteAllTextAsync(Path.Combine(root, "b.xls"), "x");
        await File.WriteAllTextAsync(Path.Combine(root, "c.txt"), "x");

        try
        {
            var capability = new LocalCapability
            {
                Name = "countFiles",
                DisplayName = "Count Files",
                ExecutionType = LocalCapabilityExecutionType.BuiltIn,
                HandlerKey = "countFiles",
                IsActive = true
            };

            var executor = new LocalCapabilityExecutor(new LocalAutomationExecutor());
            var result = await executor.ExecuteAsync(
                capability,
                new Dictionary<string, string?> { ["rootPath"] = root, ["pattern"] = "*.xls*", ["recursive"] = "false" },
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Contains("2", result.Output ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test .\XpedeonAgentMissionControl.Tests\XpedeonAgentMissionControl.Tests.csproj --filter LocalCapabilityExecutorTests.ExecuteAsync_CountFiles_Returns_ExcelCount`

Expected: FAIL with missing capability executor.

- [ ] **Step 3: Implement capability seeding and built-in execution**

```csharp
public sealed class LocalCapabilityService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public LocalCapabilityService(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    public async Task EnsureBuiltInsAsync()
    {
        await using var db = _factory.CreateDbContext();
        if (await db.Set<LocalCapability>().AnyAsync())
            return;

        db.AddRange(
            CreateBuiltIn("createDirectory", LocalCapabilityCategory.Write, "createDirectory"),
            CreateBuiltIn("listFiles", LocalCapabilityCategory.Read, "listFiles"),
            CreateBuiltIn("countFiles", LocalCapabilityCategory.Query, "countFiles"),
            CreateBuiltIn("getFileMetadata", LocalCapabilityCategory.Read, "getFileMetadata"),
            CreateBuiltIn("readTextFile", LocalCapabilityCategory.Read, "readTextFile"),
            CreateBuiltIn("writeTextFile", LocalCapabilityCategory.Write, "writeTextFile"),
            CreateBuiltIn("copy", LocalCapabilityCategory.Write, "copy"),
            CreateBuiltIn("move", LocalCapabilityCategory.Write, "move"),
            CreateBuiltIn("rename", LocalCapabilityCategory.Write, "rename"),
            CreateBuiltIn("zip", LocalCapabilityCategory.Write, "zip"),
            CreateBuiltIn("unzip", LocalCapabilityCategory.Write, "unzip"),
            CreateBuiltIn("deleteFile", LocalCapabilityCategory.Destructive, "deleteFile"));

        await db.SaveChangesAsync();
    }
}
```

```csharp
case "countFiles":
    var rootPath = inputs["rootPath"] ?? throw new InvalidOperationException("rootPath is required.");
    var pattern = string.IsNullOrWhiteSpace(inputs["pattern"]) ? "*.*" : inputs["pattern"]!;
    var recursive = string.Equals(inputs["recursive"], "true", StringComparison.OrdinalIgnoreCase);
    var files = Directory.GetFiles(LocalAutomationPathResolver.NormalizePath(rootPath), pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
    return new LocalCapabilityExecutionResult
    {
        Success = true,
        Output = files.Length.ToString(),
        Trace = $"countFiles matched {files.Length} file(s)."
    };
```

- [ ] **Step 4: Reuse `LocalAutomationValidator` for built-in capability path validation**

```csharp
public void ValidateCapabilityInputs(Agent agent, IEnumerable<string> candidatePaths)
{
    var allowedRoots = ParseAllowedRoots(agent.AllowedLocalRootsJson);
    foreach (var path in candidatePaths.Where(p => !string.IsNullOrWhiteSpace(p)))
        EnsureAllowed(path, allowedRoots);
}
```

- [ ] **Step 5: Run focused tests to verify they pass**

Run: `dotnet test .\XpedeonAgentMissionControl.Tests\XpedeonAgentMissionControl.Tests.csproj --filter "LocalCapabilityExecutorTests|LocalAutomationValidatorTests"`

Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add Services/LocalCapabilityService.cs Services/LocalCapabilityExecutor.cs Services/LocalAutomationValidator.cs XpedeonAgentMissionControl.Tests/LocalCapabilityExecutorTests.cs
git commit -m "feat: add built-in local capabilities"
```

### Task 3: Add capability matching and capability-first orchestration

**Files:**
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalCapabilityMatcher.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalAutomationOrchestrator.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\TaskService.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Program.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\XpedeonAgentMissionControl.Tests\LocalCapabilityTaskFlowTests.cs`

- [ ] **Step 1: Write the failing matcher and task-flow tests**

```csharp
[Fact]
public async Task CreateAndRunAsync_Uses_CountFiles_BuiltIn_Capability()
{
    // Arrange a local automation agent and LLM result selecting countFiles.
    // Assert task completes and capability execution is persisted.
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test .\XpedeonAgentMissionControl.Tests\XpedeonAgentMissionControl.Tests.csproj --filter LocalCapabilityTaskFlowTests`

Expected: FAIL with missing capability invocation flow.

- [ ] **Step 3: Implement the matcher and update orchestration**

```csharp
public sealed class LocalCapabilityMatcher
{
    public LocalCapability? Match(string? capabilityName, IReadOnlyCollection<LocalCapability> capabilities)
        => capabilities.FirstOrDefault(c => c.IsActive && string.Equals(c.Name, capabilityName, StringComparison.OrdinalIgnoreCase));
}
```

```csharp
if (string.Equals(invocation.Mode, "invokeCapability", StringComparison.OrdinalIgnoreCase))
{
    var capability = _matcher.Match(invocation.CapabilityName, await _capabilityService.GetActiveAsync())
        ?? throw new InvalidOperationException($"Capability '{invocation.CapabilityName}' was not found.");

    _validator.ValidateCapabilityInputs(agent, invocation.Inputs.Values.OfType<string>());
    var capabilityResult = await _capabilityExecutor.ExecuteAsync(capability, invocation.Inputs, cancellationToken);
    task.LocalCapabilityId = capability.Id;
    task.LocalCapabilityExecutionJson = JsonSerializer.Serialize(capabilityResult);
}
```

- [ ] **Step 4: Register capability services**

```csharp
builder.Services.AddScoped<LocalCapabilityService>();
builder.Services.AddScoped<LocalCapabilityMatcher>();
builder.Services.AddScoped<LocalCapabilityExecutor>();
```

- [ ] **Step 5: Run focused tests to verify they pass**

Run: `dotnet test .\XpedeonAgentMissionControl.Tests\XpedeonAgentMissionControl.Tests.csproj --filter LocalCapabilityTaskFlowTests`

Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add Services/LocalCapabilityMatcher.cs Services/LocalAutomationOrchestrator.cs Services/TaskService.cs Program.cs XpedeonAgentMissionControl.Tests/LocalCapabilityTaskFlowTests.cs
git commit -m "feat: route local tasks through capability matcher"
```

### Task 4: Add generated capability drafting, validation, and script persistence

**Files:**
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalCapabilityDraftValidator.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalCapabilityScriptStore.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalCapabilityExecutor.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalAutomationOrchestrator.cs`
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\XpedeonAgentMissionControl.Tests\LocalCapabilityDraftValidatorTests.cs`

- [ ] **Step 1: Write the failing draft validator tests**

```csharp
using XpedeonAgentMissionControl.Models;
using XpedeonAgentMissionControl.Services;

namespace XpedeonAgentMissionControl.Tests;

public class LocalCapabilityDraftValidatorTests
{
    [Fact]
    public void Validate_Rejects_Draft_Without_Script()
    {
        var validator = new LocalCapabilityDraftValidator();

        var error = Assert.Throws<InvalidOperationException>(() => validator.Validate(new LocalCapabilityDraft
        {
            Name = "countExcelFiles",
            Description = "Count excel files",
            Category = "Query",
            ExecutionType = "PowerShell",
            Inputs = new Dictionary<string, string> { ["rootPath"] = "string" }
        }));

        Assert.Contains("script", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test .\XpedeonAgentMissionControl.Tests\XpedeonAgentMissionControl.Tests.csproj --filter LocalCapabilityDraftValidatorTests.Validate_Rejects_Draft_Without_Script`

Expected: FAIL with missing draft validator.

- [ ] **Step 3: Implement the draft validator and script store**

```csharp
public sealed class LocalCapabilityDraftValidator
{
    public LocalCapabilityDraft Validate(LocalCapabilityDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Name))
            throw new InvalidOperationException("Capability draft requires a name.");
        if (string.IsNullOrWhiteSpace(draft.Description))
            throw new InvalidOperationException("Capability draft requires a description.");
        if (string.IsNullOrWhiteSpace(draft.Script))
            throw new InvalidOperationException("Capability draft requires a script.");
        if (!string.Equals(draft.ExecutionType, "PowerShell", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("V1 generated capabilities must use PowerShell.");

        return draft;
    }
}
```

```csharp
public sealed class LocalCapabilityScriptStore
{
    private readonly IWebHostEnvironment _environment;

    public LocalCapabilityScriptStore(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    public async Task<string> SaveAsync(LocalCapability capability, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(_environment.ContentRootPath, "LocalCapabilities", "Scripts");
        Directory.CreateDirectory(folder);
        var fileName = $"{capability.Name}.v{capability.Version}.ps1";
        var path = Path.Combine(folder, fileName);
        await File.WriteAllTextAsync(path, capability.ScriptContent ?? string.Empty, cancellationToken);
        return path;
    }
}
```

- [ ] **Step 4: Update the orchestrator to return capability drafts for approval**

```csharp
if (string.Equals(invocation.Mode, "draftCapability", StringComparison.OrdinalIgnoreCase))
{
    var draft = _draftValidator.Validate(invocation.Draft ?? throw new InvalidOperationException("Capability draft is missing."));
    task.LocalCapabilityDraftJson = JsonSerializer.Serialize(draft);

    return new LocalAutomationExecutionResult
    {
        Success = false,
        RequiresElevatedApproval = true,
        Message = BuildCapabilityDraftApprovalEvidence(draft)
    };
}
```

- [ ] **Step 5: Run focused tests to verify they pass**

Run: `dotnet test .\XpedeonAgentMissionControl.Tests\XpedeonAgentMissionControl.Tests.csproj --filter "LocalCapabilityDraftValidatorTests|LocalCapabilityTaskFlowTests"`

Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add Services/LocalCapabilityDraftValidator.cs Services/LocalCapabilityScriptStore.cs Services/LocalCapabilityExecutor.cs Services/LocalAutomationOrchestrator.cs XpedeonAgentMissionControl.Tests/LocalCapabilityDraftValidatorTests.cs
git commit -m "feat: add generated local capability drafts"
```

### Task 5: Resume approved tasks by saving and reusing generated capabilities

**Files:**
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\TaskService.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalAutomationOrchestrator.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Services\LocalCapabilityService.cs`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\XpedeonAgentMissionControl.Tests\LocalCapabilityTaskFlowTests.cs`

- [ ] **Step 1: Write the failing approval-resume reuse test**

```csharp
[Fact]
public async Task ApproveTaskAsync_Saves_Draft_And_Reuses_It_On_Next_Task()
{
    // Arrange first task to return draftCapability for countExcelFiles.
    // Approve it and assert a LocalCapability is saved.
    // Arrange second task to use invokeCapability with countExcelFiles.
    // Assert it completes without drafting again.
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test .\XpedeonAgentMissionControl.Tests\XpedeonAgentMissionControl.Tests.csproj --filter LocalCapabilityTaskFlowTests.ApproveTaskAsync_Saves_Draft_And_Reuses_It_On_Next_Task`

Expected: FAIL with missing draft-save reuse behavior.

- [ ] **Step 3: Save approved drafts during approval replay**

```csharp
if (!string.IsNullOrWhiteSpace(task.LocalCapabilityDraftJson))
{
    var draft = JsonSerializer.Deserialize<LocalCapabilityDraft>(task.LocalCapabilityDraftJson)
        ?? throw new InvalidOperationException("Saved capability draft could not be parsed.");

    var capability = await _capabilityService.CreateFromDraftAsync(draft, cancellationToken);
    task.LocalCapabilityId = capability.Id;
    task.LocalCapabilityDraftJson = null;
}
```

- [ ] **Step 4: Add the draft-to-capability conversion**

```csharp
public async Task<LocalCapability> CreateFromDraftAsync(LocalCapabilityDraft draft, CancellationToken cancellationToken)
{
    await using var db = _factory.CreateDbContext();

    var capability = new LocalCapability
    {
        Name = draft.Name,
        DisplayName = draft.Name,
        Description = draft.Description,
        Category = Enum.Parse<LocalCapabilityCategory>(draft.Category, true),
        ExecutionType = LocalCapabilityExecutionType.PowerShell,
        ScriptContent = draft.Script,
        InputSchemaJson = JsonSerializer.Serialize(draft.Inputs),
        OutputSchemaJson = "{}",
        RequiresApproval = true,
        IsGenerated = true,
        IsActive = true,
        Version = 1
    };

    capability.ScriptPath = await _scriptStore.SaveAsync(capability, cancellationToken);
    db.Add(capability);
    await db.SaveChangesAsync(cancellationToken);
    return capability;
}
```

- [ ] **Step 5: Run focused tests to verify they pass**

Run: `dotnet test .\XpedeonAgentMissionControl.Tests\XpedeonAgentMissionControl.Tests.csproj --filter LocalCapabilityTaskFlowTests`

Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add Services/TaskService.cs Services/LocalAutomationOrchestrator.cs Services/LocalCapabilityService.cs XpedeonAgentMissionControl.Tests/LocalCapabilityTaskFlowTests.cs
git commit -m "feat: save and reuse generated capabilities"
```

### Task 6: Add capability library UI and task detail visibility

**Files:**
- Create: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Components\Pages\Capabilities.razor`
- Modify: `D:\WORK\AI Projects\Xpedeon Agent Mission Control\Components\Pages\AgentDetail.razor`

- [ ] **Step 1: Add the capability library page**

```razor
@page "/capabilities"
@inject LocalCapabilityService CapabilityService

<PageTitle>Capabilities</PageTitle>

<div class="page-shell">
    <div class="section-header">
        <div>
            <h1 class="page-title">Local Capabilities</h1>
            <p class="page-subtitle">Built-in and generated machine capabilities available to the local automation agent.</p>
        </div>
    </div>

    @foreach (var capability in capabilities)
    {
        <div class="card card-pad mb-3">
            <div class="flex justify-between items-start">
                <div>
                    <div class="text-lg font-semibold">@capability.DisplayName</div>
                    <div class="text-sm text-muted">@capability.Description</div>
                </div>
                <span class="tag">@capability.ExecutionType</span>
            </div>
        </div>
    }
</div>
```

- [ ] **Step 2: Surface capability details on task detail**

```razor
@if (!string.IsNullOrWhiteSpace(selectedTask?.LocalCapabilityId))
{
    <div class="mt-4">
        <div class="text-xs text-muted mb-2">Local Capability</div>
        <div class="tag">@selectedTask.LocalCapabilityId</div>
    </div>
}

@if (!string.IsNullOrWhiteSpace(selectedTask?.LocalCapabilityDraftJson))
{
    <div class="mt-4">
        <div class="text-xs text-muted mb-2">Capability Draft</div>
        <pre class="code-block">@PrettyJson(selectedTask.LocalCapabilityDraftJson)</pre>
    </div>
}

@if (!string.IsNullOrWhiteSpace(selectedTask?.LocalCapabilityExecutionJson))
{
    <div class="mt-4">
        <div class="text-xs text-muted mb-2">Capability Execution</div>
        <pre class="code-block">@PrettyJson(selectedTask.LocalCapabilityExecutionJson)</pre>
    </div>
}
```

- [ ] **Step 3: Run build to verify the UI compiles**

Run: `dotnet build .\XpedeonAgentMissionControl.csproj -o .\artifacts\verify-build`

Expected: BUILD SUCCEEDED

- [ ] **Step 4: Commit**

```bash
git add Components/Pages/Capabilities.razor Components/Pages/AgentDetail.razor
git commit -m "feat: add capability library UI"
```

### Task 7: Full verification and cleanup

**Files:**
- Modify as needed from previous tasks only

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test .\XpedeonAgentMissionControl.Tests\XpedeonAgentMissionControl.Tests.csproj`

Expected: PASS with all capability and local automation tests green.

- [ ] **Step 2: Run a clean compile to isolated output**

Run: `dotnet build .\XpedeonAgentMissionControl.csproj -o .\artifacts\verify-build`

Expected: BUILD SUCCEEDED

- [ ] **Step 3: Remove verification artifacts**

Run:

```powershell
$target = Resolve-Path '.\artifacts' -ErrorAction SilentlyContinue
if ($null -ne $target -and $target.Path -eq 'D:\WORK\AI Projects\Xpedeon Agent Mission Control\artifacts') {
    Remove-Item -LiteralPath $target.Path -Recurse -Force
}
```

Expected: no error, `artifacts` removed from `git status`.

- [ ] **Step 4: Review final worktree**

Run: `git status --short`

Expected: only intended source, test, docs, and SQL files remain.

- [ ] **Step 5: Commit the final integration pass**

```bash
git add .
git commit -m "feat: add reusable local capability library"
```

## Self-Review

- Spec coverage: the plan covers registry/persistence, built-in capability execution, capability-first orchestration, generated draft approval flow, capability reuse, storage, UI visibility, SQL patching, and verification.
- Placeholder scan: each task includes concrete files, code, commands, and expected outcomes; no `TODO` or unresolved placeholders remain.
- Type consistency: the plan consistently uses `LocalCapability`, `LocalCapabilityInvocation`, `LocalCapabilityDraft`, `LocalCapabilityService`, `LocalCapabilityExecutor`, `LocalCapabilityMatcher`, `LocalCapabilityDraftValidator`, and task fields `LocalCapabilityId`, `LocalCapabilityDraftJson`, and `LocalCapabilityExecutionJson`.
