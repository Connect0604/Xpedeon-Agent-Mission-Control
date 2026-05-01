namespace XpedeonAgentMissionControl.Models;

public sealed record DomainTerms(
    string AgentSingular,
    string AgentPlural,
    string AgentCreateAction,
    string AgentTemplatePlural,
    string TaskSingular,
    string TaskPlural,
    string WorkflowSingular,
    string WorkflowPlural,
    string SwarmSingular,
    string SwarmPlural,
    string OutputPlural,
    string ProviderPlural,
    string OrchestratorTitle);

public sealed record DomainNavigationSection(
    string Overview,
    string Workforce,
    string Operations,
    string Configuration);

public sealed record DomainStatusTerms(
    string Healthy,
    string Issues,
    string Online);

public sealed record DomainPack(
    string Id,
    string DisplayName,
    string ProductName,
    string ProductTagline,
    string OverviewCrumb,
    DomainTerms Terms,
    DomainNavigationSection Navigation,
    DomainStatusTerms Status,
    IReadOnlyDictionary<string, string> PageTitles);
