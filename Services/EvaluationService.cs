using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class EvaluationService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly LLMExecutionService _llm;
    private readonly SkillService _skillService;

    public EvaluationService(
        IDbContextFactory<AppDbContext> factory,
        LLMExecutionService llm,
        SkillService skillService)
    {
        _factory = factory;
        _llm = llm;
        _skillService = skillService;
    }

    public async Task<List<EvaluationScenario>> GetScenariosAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.EvaluationScenarios
            .Include(s => s.BaseAgent)
            .OrderByDescending(s => s.IsPinned)
            .ThenByDescending(s => s.LastRunAt ?? s.CreatedAt)
            .ToListAsync();
    }

    public async Task<EvaluationScenario?> GetScenarioAsync(string id)
    {
        await using var db = _factory.CreateDbContext();
        return await db.EvaluationScenarios
            .Include(s => s.BaseAgent)
            .FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task<List<EvaluationRun>> GetRunsForScenarioAsync(string scenarioId)
    {
        await using var db = _factory.CreateDbContext();
        return await db.EvaluationRuns
            .Where(r => r.EvaluationScenarioId == scenarioId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();
    }

    public async Task<EvaluationScenario> SaveScenarioAsync(EvaluationScenario scenario)
    {
        await using var db = _factory.CreateDbContext();

        if (string.IsNullOrWhiteSpace(scenario.Id))
        {
            scenario.Id = Guid.NewGuid().ToString();
            scenario.CreatedAt = DateTime.UtcNow;
            db.EvaluationScenarios.Add(scenario);
        }
        else
        {
            db.EvaluationScenarios.Update(scenario);
        }

        await db.SaveChangesAsync();
        return scenario;
    }

    public async Task DeleteScenarioAsync(string scenarioId)
    {
        await using var db = _factory.CreateDbContext();
        var scenario = await db.EvaluationScenarios.FindAsync(scenarioId);
        if (scenario == null) return;

        db.EvaluationScenarios.Remove(scenario);
        await db.SaveChangesAsync();
    }

    public async Task<List<EvaluationRun>> RunScenarioAsync(string scenarioId, CancellationToken cancellationToken = default)
    {
        await using var db = _factory.CreateDbContext();
        var scenario = await db.EvaluationScenarios
            .Include(s => s.BaseAgent)
                .ThenInclude(a => a!.LLMProvider)
            .Include(s => s.BaseAgent)
                .ThenInclude(a => a!.MCPServers)
                    .ThenInclude(m => m.MCPServer)
            .Include(s => s.BaseAgent)
                .ThenInclude(a => a!.Skills)
                    .ThenInclude(s => s.SkillDefinition)
            .FirstOrDefaultAsync(s => s.Id == scenarioId, cancellationToken)
            ?? throw new InvalidOperationException("Evaluation scenario not found.");

        var providerIds = DeserializeStringList(scenario.SelectedProviderIdsJson);
        var skillIds = DeserializeStringList(scenario.SelectedSkillIdsJson);

        var providers = providerIds.Any()
            ? await db.LLMProviders.Where(p => providerIds.Contains(p.Id) && p.IsEnabled).ToListAsync(cancellationToken)
            : scenario.BaseAgent?.LLMProvider is not null
                ? new List<LLMProvider> { scenario.BaseAgent.LLMProvider }
                : new List<LLMProvider>();

        if (!providers.Any())
            throw new InvalidOperationException("Evaluation scenario has no enabled providers selected.");

        var selectedSkills = skillIds.Any()
            ? await _skillService.GetByIdsAsync(skillIds)
            : scenario.BaseAgent?.Skills
                .Where(s => s.IsEnabled && s.SkillDefinition != null)
                .Select(s => s.SkillDefinition!)
                .ToList() ?? new List<SkillDefinition>();

        var runs = new List<EvaluationRun>();
        foreach (var provider in providers)
        {
            var runtimeAgent = BuildEvaluationAgent(scenario, provider, selectedSkills);
            var prompt = string.IsNullOrWhiteSpace(scenario.PromptOverride)
                ? runtimeAgent.SystemPrompt
                : scenario.PromptOverride!;

            var sw = Stopwatch.StartNew();
            try
            {
                var result = await _llm.ExecuteAsync(runtimeAgent, scenario.Input, prompt, cancellationToken);
                sw.Stop();

                runs.Add(new EvaluationRun
                {
                    Id = Guid.NewGuid().ToString(),
                    EvaluationScenarioId = scenario.Id,
                    ProviderId = provider.Id,
                    ProviderName = provider.Name,
                    ModelName = provider.ModelName,
                    SkillNamesJson = JsonSerializer.Serialize(selectedSkills.Select(s => s.Name).ToList()),
                    Output = result.Output,
                    ExecutionTrace = result.Trace,
                    PromptTokens = result.PromptTokens,
                    CompletionTokens = result.CompletionTokens,
                    TotalTokens = result.TotalTokens,
                    CostUSD = result.CostUSD,
                    ToolCallsUsed = result.ToolCallsUsed,
                    DurationMs = sw.ElapsedMilliseconds,
                    Status = EvaluationRunStatus.Completed,
                    CreatedAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                runs.Add(new EvaluationRun
                {
                    Id = Guid.NewGuid().ToString(),
                    EvaluationScenarioId = scenario.Id,
                    ProviderId = provider.Id,
                    ProviderName = provider.Name,
                    ModelName = provider.ModelName,
                    SkillNamesJson = JsonSerializer.Serialize(selectedSkills.Select(s => s.Name).ToList()),
                    Output = string.Empty,
                    ExecutionTrace = ex.ToString(),
                    Status = EvaluationRunStatus.Failed,
                    ErrorMessage = ex.Message,
                    DurationMs = sw.ElapsedMilliseconds,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        scenario.LastRunAt = DateTime.UtcNow;
        db.EvaluationRuns.AddRange(runs);
        await db.SaveChangesAsync(cancellationToken);
        return runs.OrderByDescending(r => r.CreatedAt).ToList();
    }

    private static Agent BuildEvaluationAgent(EvaluationScenario scenario, LLMProvider provider, List<SkillDefinition> selectedSkills)
    {
        var baseAgent = scenario.BaseAgent;
        var systemPrompt = !string.IsNullOrWhiteSpace(scenario.PromptOverride)
            ? scenario.PromptOverride!
            : baseAgent?.SystemPrompt ?? "You are an evaluation runner. Answer the task clearly and use tools only when needed.";

        return new Agent
        {
            Id = baseAgent?.Id ?? string.Empty,
            Name = baseAgent?.Name ?? scenario.Name,
            Type = baseAgent?.Type ?? AgentType.Custom,
            Description = baseAgent?.Description ?? scenario.Description,
            LLMProviderId = provider.Id,
            LLMProvider = provider,
            SystemPrompt = systemPrompt,
            RequiresApproval = baseAgent?.RequiresApproval ?? false,
            ConfidenceThreshold = baseAgent?.ConfidenceThreshold ?? 0.8,
            MCPServers = baseAgent?.MCPServers ?? new List<AgentMCPServer>(),
            Skills = selectedSkills.Select(s => new AgentSkillAssignment
            {
                AgentId = baseAgent?.Id ?? string.Empty,
                SkillDefinitionId = s.Id,
                IsEnabled = true,
                SkillDefinition = s
            }).ToList()
        };
    }

    private static List<string> DeserializeStringList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<string>();

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}
