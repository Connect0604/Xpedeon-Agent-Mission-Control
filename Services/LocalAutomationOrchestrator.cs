using System.Text;
using System.Text.Json;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public sealed class LocalAutomationOrchestrator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly LLMExecutionService _llm;
    private readonly LocalAutomationValidator _validator;
    private readonly LocalAutomationExecutor _executor;
    private readonly LocalCapabilityService? _capabilityService;
    private readonly LocalCapabilityMatcher? _capabilityMatcher;
    private readonly LocalCapabilityExecutor? _capabilityExecutor;
    private readonly LocalCapabilityDraftValidator _draftValidator;
    private readonly LocalCapabilityScriptStore _scriptStore;

    public LocalAutomationOrchestrator(
        LLMExecutionService llm,
        LocalAutomationValidator validator,
        LocalAutomationExecutor executor)
        : this(llm, validator, executor, null, null, null, null, null)
    {
    }

    public LocalAutomationOrchestrator(
        LLMExecutionService llm,
        LocalAutomationValidator validator,
        LocalAutomationExecutor executor,
        LocalCapabilityService? capabilityService,
        LocalCapabilityMatcher? capabilityMatcher,
        LocalCapabilityExecutor? capabilityExecutor)
        : this(llm, validator, executor, capabilityService, capabilityMatcher, capabilityExecutor, null, null)
    {
    }

    public LocalAutomationOrchestrator(
        LLMExecutionService llm,
        LocalAutomationValidator validator,
        LocalAutomationExecutor executor,
        LocalCapabilityService? capabilityService,
        LocalCapabilityMatcher? capabilityMatcher,
        LocalCapabilityExecutor? capabilityExecutor,
        LocalCapabilityDraftValidator? draftValidator,
        LocalCapabilityScriptStore? scriptStore)
    {
        _llm = llm;
        _validator = validator;
        _executor = executor;
        _capabilityService = capabilityService;
        _capabilityMatcher = capabilityMatcher;
        _capabilityExecutor = capabilityExecutor;
        _draftValidator = draftValidator ?? new LocalCapabilityDraftValidator();
        _scriptStore = scriptStore ?? new LocalCapabilityScriptStore();
    }

    public async Task<LocalOrchestrationExecutionResult> PlanAndExecuteAsync(
        Agent agent,
        AgentTask task,
        CancellationToken cancellationToken,
        bool skipApproval = false,
        string? planningInputOverride = null,
        IReadOnlyList<string>? recentTouchedPaths = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(task);

        var planResult = await LoadOrCreatePlanAsync(agent, task, cancellationToken, planningInputOverride, recentTouchedPaths);

        if (planResult.Capability is not null)
        {
            return await ExecuteCapabilityAsync(agent, task, planResult, skipApproval, cancellationToken);
        }

        if (planResult.Draft is not null)
        {
            return skipApproval
                ? await SaveAndExecuteDraftAsync(agent, task, planResult, cancellationToken)
                : BuildDraftResult(planResult);
        }

        return await ExecuteProvidedPlanAsync(agent, task, planResult.Plan!, cancellationToken, skipApproval, planResult.Llm);
    }

    public async Task<LocalOrchestrationExecutionResult> ExecuteProvidedPlanAsync(
        Agent agent,
        AgentTask task,
        LocalAutomationPlan plan,
        CancellationToken cancellationToken,
        bool skipApproval = false,
        LLMResult? llm = null)
    {
        var validatedPlan = _validator.Validate(agent, plan);
        task.LocalActionPlanJson = JsonSerializer.Serialize(validatedPlan, JsonOptions);

        if (!skipApproval && RequiresApproval(agent, validatedPlan))
        {
            return new LocalOrchestrationExecutionResult
            {
                Success = false,
                RequiresElevatedApproval = true,
                Message = BuildApprovalEvidence(validatedPlan),
                Plan = validatedPlan,
                TouchedPaths = CollectTouchedPaths(validatedPlan),
                PromptTokens = llm?.PromptTokens ?? 0,
                CompletionTokens = llm?.CompletionTokens ?? 0,
                TotalTokens = llm?.TotalTokens ?? 0,
                CostUSD = llm?.CostUSD ?? 0,
                ModelUsed = llm?.ModelUsed,
                ConfidenceScore = llm?.ConfidenceScore
            };
        }

        var execution = await _executor.ExecuteAsync(validatedPlan, cancellationToken);
        return BuildAutomationResult(execution, llm);
    }

    private LocalOrchestrationExecutionResult BuildDraftResult(PlanBuildResult planResult)
    {
        var draft = planResult.Draft
            ?? throw new InvalidOperationException("Capability draft details are required.");
        var invocation = planResult.Invocation
            ?? throw new InvalidOperationException("Capability draft invocation details are required.");
        var scriptPath = planResult.DraftScriptPath
            ?? throw new InvalidOperationException("Capability draft script path is required.");
        var payloadJson = SerializeCapabilityDraftPayload(invocation, scriptPath, null);

        return new LocalOrchestrationExecutionResult
        {
            Success = false,
            RequiresElevatedApproval = true,
            Message = BuildCapabilityDraftApprovalEvidence(planResult.DraftSummary, invocation, draft, scriptPath),
            Output = payloadJson,
            TouchedPaths = CollectDraftTouchedPaths(invocation, scriptPath),
            PromptTokens = planResult.Llm?.PromptTokens ?? 0,
            CompletionTokens = planResult.Llm?.CompletionTokens ?? 0,
            TotalTokens = planResult.Llm?.TotalTokens ?? 0,
            CostUSD = planResult.Llm?.CostUSD ?? 0,
            ModelUsed = planResult.Llm?.ModelUsed,
            ConfidenceScore = planResult.Llm?.ConfidenceScore,
            LocalCapabilityDraftScriptPath = scriptPath
        };
    }

    private async Task<LocalOrchestrationExecutionResult> SaveAndExecuteDraftAsync(
        Agent agent,
        AgentTask task,
        PlanBuildResult planResult,
        CancellationToken cancellationToken)
    {
        if (_capabilityService is null || _capabilityExecutor is null)
        {
            throw new InvalidOperationException("Generated capability persistence is not registered.");
        }

        var draft = planResult.Draft
            ?? throw new InvalidOperationException("Capability draft details are required.");
        var invocation = planResult.Invocation
            ?? throw new InvalidOperationException("Capability draft invocation details are required.");
        var scriptPath = planResult.DraftScriptPath
            ?? throw new InvalidOperationException("Capability draft script path is required.");

        var capability = await _capabilityService.SaveGeneratedAsync(
            draft,
            scriptPath,
            agent.AllowedLocalRootsJson,
            cancellationToken);

        task.LocalCapabilityId = capability.Id;
        task.LocalCapabilityDraftJson = SerializeCapabilityDraftPayload(invocation, scriptPath, capability);

        var execution = await _capabilityExecutor.ExecuteAsync(capability, invocation.Inputs, cancellationToken);
        task.LocalCapabilityExecutionJson = execution.Output;

        return new LocalOrchestrationExecutionResult
        {
            Success = execution.Success,
            RequiresElevatedApproval = execution.RequiresElevatedApproval,
            Message = execution.Message,
            Trace = execution.Trace,
            Output = execution.Output,
            LocalCapabilityId = capability.Id,
            LocalCapabilityExecutionJson = execution.Output,
            TouchedPaths = CollectCapabilityTouchedPaths(invocation),
            PromptTokens = planResult.Llm?.PromptTokens ?? 0,
            CompletionTokens = planResult.Llm?.CompletionTokens ?? 0,
            TotalTokens = planResult.Llm?.TotalTokens ?? 0,
            CostUSD = planResult.Llm?.CostUSD ?? 0,
            ModelUsed = planResult.Llm?.ModelUsed,
            ConfidenceScore = planResult.Llm?.ConfidenceScore
        };
    }

    private async Task<LocalOrchestrationExecutionResult> ExecuteCapabilityAsync(
        Agent agent,
        AgentTask task,
        PlanBuildResult planResult,
        bool skipApproval,
        CancellationToken cancellationToken)
    {
        var capability = planResult.Capability
            ?? throw new InvalidOperationException("Capability execution requires a matched capability.");
        var invocation = planResult.Invocation
            ?? throw new InvalidOperationException("Capability execution requires invocation details.");

        if (!skipApproval && RequiresApproval(agent, capability))
        {
            return new LocalOrchestrationExecutionResult
            {
                Success = false,
                RequiresElevatedApproval = true,
                Message = BuildCapabilityApprovalEvidence(capability, invocation),
                LocalCapabilityId = capability.Id,
                PromptTokens = planResult.Llm?.PromptTokens ?? 0,
                CompletionTokens = planResult.Llm?.CompletionTokens ?? 0,
                TotalTokens = planResult.Llm?.TotalTokens ?? 0,
                CostUSD = planResult.Llm?.CostUSD ?? 0,
                ModelUsed = planResult.Llm?.ModelUsed,
                ConfidenceScore = planResult.Llm?.ConfidenceScore
            };
        }

        if (_capabilityExecutor is null)
        {
            throw new InvalidOperationException("Local capability execution is not registered.");
        }

        var execution = await _capabilityExecutor.ExecuteAsync(capability, invocation.Inputs, cancellationToken);
        task.LocalCapabilityId = capability.Id;
        task.LocalCapabilityExecutionJson = execution.Output;

        return new LocalOrchestrationExecutionResult
        {
            Success = execution.Success,
            RequiresElevatedApproval = execution.RequiresElevatedApproval,
            Message = execution.Message,
            Trace = execution.Trace,
            Output = execution.Output,
            LocalCapabilityId = capability.Id,
            LocalCapabilityExecutionJson = execution.Output,
            TouchedPaths = CollectCapabilityTouchedPaths(invocation),
            PromptTokens = planResult.Llm?.PromptTokens ?? 0,
            CompletionTokens = planResult.Llm?.CompletionTokens ?? 0,
            TotalTokens = planResult.Llm?.TotalTokens ?? 0,
            CostUSD = planResult.Llm?.CostUSD ?? 0,
            ModelUsed = planResult.Llm?.ModelUsed,
            ConfidenceScore = planResult.Llm?.ConfidenceScore
        };
    }

    private static LocalOrchestrationExecutionResult BuildAutomationResult(LocalAutomationExecutionResult execution, LLMResult? llm)
    {
        execution.PromptTokens = llm?.PromptTokens ?? 0;
        execution.CompletionTokens = llm?.CompletionTokens ?? 0;
        execution.TotalTokens = llm?.TotalTokens ?? 0;
        execution.CostUSD = llm?.CostUSD ?? 0;
        execution.ModelUsed = llm?.ModelUsed;
        execution.ConfidenceScore = llm?.ConfidenceScore;
        return new LocalOrchestrationExecutionResult
        {
            Success = execution.Success,
            RequiresElevatedApproval = execution.RequiresElevatedApproval,
            Message = execution.Message,
            Trace = execution.Trace,
            Plan = execution.Plan,
            TouchedPaths = execution.TouchedPaths,
            ActionResults = execution.ActionResults,
            PromptTokens = execution.PromptTokens,
            CompletionTokens = execution.CompletionTokens,
            TotalTokens = execution.TotalTokens,
            CostUSD = execution.CostUSD,
            ModelUsed = execution.ModelUsed,
            ConfidenceScore = execution.ConfidenceScore
        };
    }

    private async Task<PlanBuildResult> LoadOrCreatePlanAsync(
        Agent agent,
        AgentTask task,
        CancellationToken cancellationToken,
        string? planningInputOverride,
        IReadOnlyList<string>? recentTouchedPaths)
    {
        if (!string.IsNullOrWhiteSpace(task.LocalCapabilityDraftJson))
        {
            var savedMode = TryReadMode(task.LocalCapabilityDraftJson);
            if (string.Equals(savedMode, "invokeCapability", StringComparison.OrdinalIgnoreCase))
            {
                var reviewedInvocation = TryDeserializeInvocation(task.LocalCapabilityDraftJson)
                    ?? throw new InvalidOperationException("Saved local capability invocation payload could not be parsed.");
                var reviewedCapability = await ResolveCapabilityAsync(reviewedInvocation, cancellationToken);
                return new PlanBuildResult(null, null, reviewedInvocation, reviewedCapability, null, null, null);
            }

            if (string.Equals(savedMode, "draftCapability", StringComparison.OrdinalIgnoreCase))
            {
                var reviewedDraftInvocation = TryDeserializeInvocation(task.LocalCapabilityDraftJson)
                    ?? throw new InvalidOperationException("Saved local capability draft payload could not be parsed.");
                var reviewedDraft = reviewedDraftInvocation.Draft
                    ?? throw new InvalidOperationException("Saved local capability draft payload is missing draft details.");
                return new PlanBuildResult(
                    null,
                    null,
                    reviewedDraftInvocation,
                    null,
                    reviewedDraft,
                    TryReadDraftScriptPath(task.LocalCapabilityDraftJson),
                    reviewedDraftInvocation.Summary);
            }

            throw new InvalidOperationException("Saved local capability draft payload could not be parsed.");
        }

        if (!string.IsNullOrWhiteSpace(task.LocalActionPlanJson))
        {
            var existing = JsonSerializer.Deserialize<LocalAutomationPlan>(task.LocalActionPlanJson, JsonOptions)
                ?? throw new InvalidOperationException("Saved local automation plan could not be parsed.");
            return new PlanBuildResult(existing, null, null, null, null, null, null);
        }

        var planningPrompt = await BuildPlanningPrompt(task.SystemPromptSnapshot ?? agent.SystemPrompt);
        var llmResult = await _llm.ExecuteAsync(agent, planningInputOverride ?? task.Input ?? string.Empty, planningPrompt, cancellationToken, task.Id);
        if (RequiresCapabilityCorrection(llmResult.Output))
        {
            llmResult = await _llm.ExecuteAsync(
                agent,
                planningInputOverride ?? task.Input ?? string.Empty,
                BuildCapabilityCorrectionPrompt(planningPrompt, llmResult.Output),
                cancellationToken,
                task.Id);
        }

        var capabilityMatch = await TryBuildCapabilityInvocationAsync(llmResult.Output, cancellationToken);
        if (capabilityMatch is not null)
        {
            capabilityMatch = (ApplyRecentPathContext(task.Input ?? string.Empty, recentTouchedPaths, capabilityMatch.Value.Invocation), capabilityMatch.Value.Capability);
            task.LocalCapabilityDraftJson = JsonSerializer.Serialize(capabilityMatch.Value.Invocation, JsonOptions);
            task.LocalCapabilityId = capabilityMatch.Value.Capability.Id;
            return new PlanBuildResult(null, llmResult, capabilityMatch.Value.Invocation, capabilityMatch.Value.Capability, null, null, null);
        }

        var draftMatch = await TryBuildCapabilityDraftAsync(llmResult.Output, cancellationToken);
        if (draftMatch is not null)
        {
            task.LocalCapabilityDraftJson = SerializeCapabilityDraftPayload(draftMatch.Value.Invocation, draftMatch.Value.ScriptPath, null);
            return new PlanBuildResult(null, llmResult, draftMatch.Value.Invocation, null, draftMatch.Value.Draft, draftMatch.Value.ScriptPath, draftMatch.Value.Summary);
        }

        var plan = JsonSerializer.Deserialize<LocalAutomationPlan>(llmResult.Output, JsonOptions)
            ?? throw new InvalidOperationException("Planner did not return a valid local automation plan or capability invocation.");
        plan = ApplyRecentPathContext(task.Input ?? string.Empty, recentTouchedPaths, plan);

        return new PlanBuildResult(plan, llmResult, null, null, null, null, null);
    }

    private static bool RequiresCapabilityCorrection(string output)
    {
        var invocation = TryDeserializeInvocation(output);
        return invocation is not null &&
               string.Equals(invocation.Mode, "invokeCapability", StringComparison.OrdinalIgnoreCase) &&
               string.IsNullOrWhiteSpace(invocation.CapabilityName);
    }

    private static string BuildCapabilityCorrectionPrompt(string planningPrompt, string invalidOutput)
    {
        var prompt = new StringBuilder(planningPrompt.Trim());
        prompt.AppendLine();
        prompt.AppendLine();
        prompt.AppendLine("Your previous response was invalid.");
        prompt.AppendLine("If mode is \"invokeCapability\", capabilityName is required and cannot be empty.");
        prompt.AppendLine("If you are not invoking an existing capability, return either a valid draftCapability payload or a raw automation plan with actions.");
        prompt.AppendLine("Correct this previous JSON and return only corrected JSON:");
        prompt.AppendLine(invalidOutput);
        return prompt.ToString();
    }

    private static LocalAutomationPlan ApplyRecentPathContext(
        string userInput,
        IReadOnlyList<string>? recentTouchedPaths,
        LocalAutomationPlan plan)
    {
        var matchedFolder = TryResolveRecentFolder(userInput, recentTouchedPaths);
        if (string.IsNullOrWhiteSpace(matchedFolder))
        {
            return plan;
        }

        foreach (var action in plan.Actions)
        {
            action.Path = RewriteAgainstMatchedFolder(action.Path, matchedFolder);
            action.Source = RewriteAgainstMatchedFolder(action.Source, matchedFolder);
            action.Destination = RewriteAgainstMatchedFolder(action.Destination, matchedFolder);
        }

        return plan;
    }

    private static LocalCapabilityInvocation ApplyRecentPathContext(
        string userInput,
        IReadOnlyList<string>? recentTouchedPaths,
        LocalCapabilityInvocation invocation)
    {
        var matchedFolder = TryResolveRecentFolder(userInput, recentTouchedPaths);
        if (string.IsNullOrWhiteSpace(matchedFolder))
        {
            return invocation;
        }

        foreach (var key in invocation.Inputs.Keys.ToList())
        {
            invocation.Inputs[key] = RewriteAgainstMatchedFolder(invocation.Inputs[key], matchedFolder, key);
        }

        return invocation;
    }

    private static string? TryResolveRecentFolder(string userInput, IReadOnlyList<string>? recentTouchedPaths)
    {
        if (string.IsNullOrWhiteSpace(userInput) || recentTouchedPaths is null || recentTouchedPaths.Count == 0)
        {
            return null;
        }

        var matches = recentTouchedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => LocalAutomationPathResolver.NormalizePath(path))
            .Where(path =>
            {
                var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return !string.IsNullOrWhiteSpace(name) &&
                       userInput.Contains(name, StringComparison.OrdinalIgnoreCase);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    private static string? RewriteAgainstMatchedFolder(string? candidatePath, string matchedFolder, string? inputKey = null)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return candidatePath;
        }

        var matchedFolderName = Path.GetFileName(matchedFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(matchedFolderName))
        {
            return candidatePath;
        }

        if (string.Equals(inputKey, "rootPath", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedCandidate = LocalAutomationPathResolver.NormalizePath(candidatePath);
            var candidateFolderName = Path.GetFileName(normalizedCandidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(candidateFolderName, matchedFolderName, StringComparison.OrdinalIgnoreCase))
            {
                return matchedFolder;
            }
        }

        if (!Path.IsPathRooted(candidatePath))
        {
            if (candidatePath.StartsWith(matchedFolderName + "\\", StringComparison.OrdinalIgnoreCase) ||
                candidatePath.StartsWith(matchedFolderName + "/", StringComparison.OrdinalIgnoreCase))
            {
                var relative = candidatePath[(matchedFolderName.Length + 1)..].TrimStart('\\', '/');
                return string.IsNullOrWhiteSpace(relative) ? matchedFolder : Path.Combine(matchedFolder, relative);
            }

            return candidatePath;
        }

        var normalized = LocalAutomationPathResolver.NormalizePath(candidatePath);
        var parent = Path.GetDirectoryName(normalized);
        var parentName = string.IsNullOrWhiteSpace(parent)
            ? string.Empty
            : Path.GetFileName(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (string.Equals(parentName, matchedFolderName, StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith(matchedFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(parent, matchedFolder, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(matchedFolder, Path.GetFileName(normalized));
        }

        return candidatePath;
    }

    private static bool RequiresApproval(Agent agent, LocalAutomationPlan plan)
    {
        if (agent.RequiresApproval)
            return true;

        if (agent.LocalAutomationApprovalMode == LocalAutomationApprovalMode.FullControl)
            return false;

        if (agent.LocalAutomationApprovalMode == LocalAutomationApprovalMode.Restricted)
            return true;

        return plan.Actions.Any(action =>
            action.Type == LocalAutomationActionType.Delete ||
            action.Type == LocalAutomationActionType.RunPowerShell ||
            action.Overwrite);
    }

    private static bool RequiresApproval(Agent agent, LocalCapability capability)
    {
        if (agent.RequiresApproval)
            return true;

        return agent.LocalAutomationApprovalMode switch
        {
            LocalAutomationApprovalMode.FullControl => false,
            LocalAutomationApprovalMode.Restricted => true,
            _ => capability.RequiresApproval
        };
    }

    private async Task<(LocalCapabilityInvocation Invocation, LocalCapability Capability)?> TryBuildCapabilityInvocationAsync(
        string output,
        CancellationToken cancellationToken)
    {
        if (_capabilityService is null || _capabilityMatcher is null || _capabilityExecutor is null)
        {
            return null;
        }

        var invocation = TryDeserializeInvocation(output);
        if (invocation is null ||
            !string.Equals(invocation.Mode, "invokeCapability", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(invocation.CapabilityName))
        {
            return null;
        }

        var capabilities = await _capabilityService.GetAllAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return (invocation, ResolveCapability(invocation, capabilities));
    }

    private async Task<(LocalCapabilityInvocation Invocation, LocalCapabilityDraft Draft, string ScriptPath, string Summary)?> TryBuildCapabilityDraftAsync(
        string output,
        CancellationToken cancellationToken)
    {
        var invocation = TryDeserializeInvocation(output);
        if (invocation is null ||
            !string.Equals(invocation.Mode, "draftCapability", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (invocation.Draft is null)
        {
            throw new InvalidOperationException("Planner returned draftCapability without draft details.");
        }

        var draft = _draftValidator.Validate(invocation.Draft);
        var scriptPath = await _scriptStore.SaveDraftAsync(draft, cancellationToken);
        return (invocation, draft, scriptPath, invocation.Summary);
    }

    private async Task<string> BuildPlanningPrompt(string systemPrompt)
    {
        var prompt = new StringBuilder(systemPrompt.Trim());
        if (prompt.Length > 0)
            prompt.AppendLine().AppendLine();

        prompt.AppendLine("Return only JSON.");
        prompt.AppendLine("Prefer existing local capabilities before raw automation plans.");
        if (_capabilityService is not null)
        {
            var capabilities = await _capabilityService.GetAllAsync();
            if (capabilities.Count > 0)
            {
                prompt.AppendLine("If an existing capability fits, use this shape:");
                prompt.AppendLine("{\"summary\":\"short summary\",\"mode\":\"invokeCapability\",\"capabilityName\":\"countFiles\",\"inputs\":{\"rootPath\":\"C:\\\\path\",\"pattern\":\"*.xls*\",\"recursive\":\"false\"}}");
                prompt.AppendLine("Available capabilities:");
                foreach (var capability in capabilities.Where(c => c.IsActive).OrderBy(c => c.Name))
                {
                    prompt.AppendLine($"- {capability.Name}: {capability.Description} Inputs: {capability.InputSchemaJson}");
                }
            }
        }

        prompt.AppendLine("If no existing capability fits but you can propose a reusable capability draft for approval, use this shape:");
        prompt.AppendLine("{\"summary\":\"short summary\",\"mode\":\"draftCapability\",\"inputs\":{\"rootPath\":\"C:\\\\path\",\"pattern\":\"*.xls*\"},\"draft\":{\"name\":\"countExcelFiles\",\"description\":\"Count Excel files in a folder\",\"category\":\"Query\",\"executionType\":\"PowerShell\",\"inputs\":{\"rootPath\":\"string\",\"pattern\":\"string\"},\"script\":\"param([string]$rootPath,[string]$pattern='*.xls*')\\n(Get-ChildItem -Path $rootPath -Filter $pattern -File).Count | ConvertTo-Json\"}}");
        prompt.AppendLine("For v1 drafts, always include name, description, script, executionType=\"PowerShell\", and the exact inputs to run after approval.");
        prompt.AppendLine("If no capability fits, use this automation plan shape:");
        prompt.AppendLine("{\"summary\":\"short summary\",\"actions\":[...]}");
        prompt.AppendLine("Allowed raw action types: createDirectory, copy, move, rename, delete, writeTextFile, zip, unzip, runPowerShell.");
        prompt.AppendLine("Use lowercase string enum values like \"copy\" and \"delete\" for raw actions.");
        prompt.AppendLine("Do not include markdown fences or commentary.");
        return prompt.ToString();
    }

    private static List<string> CollectTouchedPaths(LocalAutomationPlan plan)
    {
        return plan.Actions
            .SelectMany(action => new[] { action.Source, action.Destination, action.Path })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
    }

    private static string BuildApprovalEvidence(LocalAutomationPlan plan)
    {
        var lines = new List<string>
        {
            $"Summary: {plan.Summary}",
            "Planned actions:"
        };

        lines.AddRange(plan.Actions.Select(action =>
            $"- {action.Type}: {string.Join(" | ", new[] { action.Source, action.Destination, action.Path }.Where(x => !string.IsNullOrWhiteSpace(x)))}"));

        var touchedPaths = CollectTouchedPaths(plan);
        if (touchedPaths.Count > 0)
        {
            lines.Add("Touched paths:");
            lines.AddRange(touchedPaths.Select(path => $"- {path}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildCapabilityApprovalEvidence(LocalCapability capability, LocalCapabilityInvocation invocation)
    {
        var lines = new List<string>
        {
            $"Summary: {invocation.Summary}",
            $"Capability: {capability.DisplayName} ({capability.Name})",
            "Inputs:"
        };

        lines.AddRange(invocation.Inputs.Select(input => $"- {input.Key}: {input.Value}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildCapabilityDraftApprovalEvidence(
        string? summary,
        LocalCapabilityInvocation invocation,
        LocalCapabilityDraft draft,
        string scriptPath)
    {
        var lines = new List<string>
        {
            $"Summary: {summary ?? draft.Description}",
            $"Draft capability: {draft.Name}",
            $"Description: {draft.Description}",
            $"Category: {draft.Category}",
            $"Execution type: {draft.ExecutionType}",
            $"Stored script path: {scriptPath}",
            "Invocation inputs:"
        };

        if (invocation.Inputs.Count == 0)
        {
            lines.Add("- (none)");
        }
        else
        {
            lines.AddRange(invocation.Inputs.Select(input => $"- {input.Key}: {input.Value}"));
        }

        lines.Add("Input schema:");
        if (draft.Inputs.Count == 0)
        {
            lines.Add("- (none)");
        }
        else
        {
            lines.AddRange(draft.Inputs.Select(input => $"- {input.Key}: {input.Value}"));
        }

        lines.Add("Script:");
        lines.Add(draft.Script);
        return string.Join(Environment.NewLine, lines);
    }

    private static List<string> CollectCapabilityTouchedPaths(LocalCapabilityInvocation invocation)
    {
        return invocation.Inputs
            .Where(input => IsPathLikeKey(input.Key) && !string.IsNullOrWhiteSpace(input.Value))
            .Select(input => input.Value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> CollectDraftTouchedPaths(LocalCapabilityInvocation invocation, string scriptPath)
    {
        return CollectCapabilityTouchedPaths(invocation)
            .Append(scriptPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsPathLikeKey(string key)
        => key.Contains("path", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("source", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("destination", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("root", StringComparison.OrdinalIgnoreCase);

    private async Task<LocalCapability> ResolveCapabilityAsync(LocalCapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        if (_capabilityService is null)
        {
            throw new InvalidOperationException("Local capability service is not registered.");
        }

        var capabilities = await _capabilityService.GetAllAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return ResolveCapability(invocation, capabilities);
    }

    private LocalCapability ResolveCapability(
        LocalCapabilityInvocation invocation,
        IReadOnlyCollection<LocalCapability> capabilities)
    {
        if (_capabilityMatcher is null)
        {
            throw new InvalidOperationException("Local capability matcher is not registered.");
        }

        var capability = _capabilityMatcher.Match(invocation, capabilities);
        if (capability is null)
        {
            throw new InvalidOperationException($"Planner requested unknown local capability '{invocation.CapabilityName}'.");
        }

        return capability;
    }

    private static LocalCapabilityInvocation? TryDeserializeInvocation(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<LocalCapabilityInvocation>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadMode(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("mode", out var modeElement)
                ? modeElement.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadDraftScriptPath(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("draftScriptPath", out var pathElement)
                ? pathElement.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SerializeCapabilityDraftPayload(
        LocalCapabilityInvocation invocation,
        string? draftScriptPath,
        LocalCapability? savedCapability)
    {
        var payload = new
        {
            invocation.Summary,
            invocation.Mode,
            invocation.CapabilityName,
            invocation.Inputs,
            invocation.Draft,
            draftScriptPath,
            savedCapability = savedCapability is null
                ? null
                : new
                {
                    savedCapability.Id,
                    savedCapability.Name,
                    savedCapability.ScriptPath,
                    savedCapability.Version
                }
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private sealed record PlanBuildResult(
        LocalAutomationPlan? Plan,
        LLMResult? Llm,
        LocalCapabilityInvocation? Invocation,
        LocalCapability? Capability,
        LocalCapabilityDraft? Draft,
        string? DraftScriptPath,
        string? DraftSummary);
}

public sealed class LocalOrchestrationExecutionResult
{
    public bool Success { get; set; }
    public bool RequiresElevatedApproval { get; set; }
    public string? Message { get; set; }
    public string? Trace { get; set; }
    public string? Output { get; set; }
    public LocalAutomationPlan? Plan { get; set; }
    public List<string> TouchedPaths { get; set; } = new();
    public List<LocalAutomationActionResult> ActionResults { get; set; } = new();
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public decimal CostUSD { get; set; }
    public string? ModelUsed { get; set; }
    public double? ConfidenceScore { get; set; }
    public string? LocalCapabilityId { get; set; }
    public string? LocalCapabilityExecutionJson { get; set; }
    public string? LocalCapabilityDraftScriptPath { get; set; }
}
