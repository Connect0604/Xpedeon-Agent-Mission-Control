using Microsoft.Extensions.Options;
using XpedeonAgentMissionControl.Configuration;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class DomainPresentationService
{
    private static readonly IReadOnlyDictionary<string, DomainPack> Packs =
        new Dictionary<string, DomainPack>(StringComparer.OrdinalIgnoreCase)
        {
            ["DefaultMissionControl"] = new(
                Id: "DefaultMissionControl",
                DisplayName: "Default Mission Control",
                ProductName: "Xpedeon",
                ProductTagline: "Mission Control",
                OverviewCrumb: "Mission Control",
                Terms: new DomainTerms(
                    AgentSingular: "Agent",
                    AgentPlural: "Agents",
                    AgentCreateAction: "Create Agent",
                    AgentTemplatePlural: "Agent Templates",
                    TaskSingular: "Task",
                    TaskPlural: "Tasks",
                    WorkflowSingular: "Workflow",
                    WorkflowPlural: "Workflows",
                    SwarmSingular: "Swarm",
                    SwarmPlural: "Swarms",
                    OutputPlural: "Outputs",
                    ProviderPlural: "LLM Providers",
                    OrchestratorTitle: "Orchestrator"),
                Navigation: new DomainNavigationSection(
                    Overview: "Overview",
                    Workforce: "Agents",
                    Operations: "Operations",
                    Configuration: "Configuration"),
                Status: new DomainStatusTerms(
                    Healthy: "Healthy",
                    Issues: "issues",
                    Online: "online"),
                PageTitles: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["/"] = "Dashboard",
                    ["/agents"] = "Agents",
                    ["/agents/create"] = "Create Agent",
                    ["/tasks"] = "Tasks",
                    ["/workflows"] = "Workflows",
                    ["/templates"] = "Agent Templates",
                    ["/swarms"] = "Swarms",
                    ["/providers"] = "LLM Providers",
                    ["/outputs"] = "Outputs",
                    ["/logs"] = "Logs",
                    ["/mcp"] = "MCP Servers"
                }),
            ["DigitalBackOffice"] = new(
                Id: "DigitalBackOffice",
                DisplayName: "Digital Back Office",
                ProductName: "Xpedeon",
                ProductTagline: "Digital Back Office",
                OverviewCrumb: "Back Office",
                Terms: new DomainTerms(
                    AgentSingular: "Staff Member",
                    AgentPlural: "Staff",
                    AgentCreateAction: "Add Staff Member",
                    AgentTemplatePlural: "Role Templates",
                    TaskSingular: "Work Item",
                    TaskPlural: "Work Items",
                    WorkflowSingular: "Playbook",
                    WorkflowPlural: "Playbooks",
                    SwarmSingular: "Team",
                    SwarmPlural: "Teams",
                    OutputPlural: "Deliverables",
                    ProviderPlural: "AI Providers",
                    OrchestratorTitle: "Dispatch Desk"),
                Navigation: new DomainNavigationSection(
                    Overview: "Company Overview",
                    Workforce: "Workforce",
                    Operations: "Operations",
                    Configuration: "Platform"),
                Status: new DomainStatusTerms(
                    Healthy: "Healthy",
                    Issues: "issues",
                    Online: "active"),
                PageTitles: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["/"] = "Company Overview",
                    ["/agents"] = "Staff Directory",
                    ["/agents/create"] = "Add Staff Member",
                    ["/tasks"] = "Work Intake",
                    ["/workflows"] = "Playbooks",
                    ["/templates"] = "Role Templates",
                    ["/swarms"] = "Teams",
                    ["/providers"] = "AI Providers",
                    ["/outputs"] = "Deliverables",
                    ["/logs"] = "Operations Log",
                    ["/mcp"] = "Connected Systems"
                }),
            ["ShowroomOperations"] = new(
                Id: "ShowroomOperations",
                DisplayName: "Showroom Operations",
                ProductName: "Xpedeon",
                ProductTagline: "Showroom Operations",
                OverviewCrumb: "Showroom Floor",
                Terms: new DomainTerms(
                    AgentSingular: "Sales Staff",
                    AgentPlural: "Sales Staff",
                    AgentCreateAction: "Add Sales Staff",
                    AgentTemplatePlural: "Showroom Roles",
                    TaskSingular: "Customer Lead",
                    TaskPlural: "Customer Leads",
                    WorkflowSingular: "Customer Journey",
                    WorkflowPlural: "Customer Journeys",
                    SwarmSingular: "Desk Team",
                    SwarmPlural: "Desk Teams",
                    OutputPlural: "Customer Outcomes",
                    ProviderPlural: "AI Assistants",
                    OrchestratorTitle: "Floor Coordinator"),
                Navigation: new DomainNavigationSection(
                    Overview: "Showroom Overview",
                    Workforce: "Sales Staff",
                    Operations: "Customer Flow",
                    Configuration: "Operations Setup"),
                Status: new DomainStatusTerms(
                    Healthy: "Ready",
                    Issues: "issues",
                    Online: "on floor"),
                PageTitles: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["/"] = "Showroom Floor",
                    ["/agents"] = "Sales Staff",
                    ["/agents/create"] = "Add Sales Staff",
                    ["/tasks"] = "Lead Queue",
                    ["/workflows"] = "Customer Journeys",
                    ["/templates"] = "Showroom Roles",
                    ["/swarms"] = "Desk Teams",
                    ["/providers"] = "AI Assistants",
                    ["/outputs"] = "Customer Outcomes",
                    ["/logs"] = "Showroom Activity",
                    ["/mcp"] = "Connected Systems"
                }),
            ["RetailOperations"] = new(
                Id: "RetailOperations",
                DisplayName: "Retail Operations",
                ProductName: "Xpedeon",
                ProductTagline: "Retail Operations",
                OverviewCrumb: "Store Operations",
                Terms: new DomainTerms(
                    AgentSingular: "Store Staff",
                    AgentPlural: "Store Staff",
                    AgentCreateAction: "Add Store Staff",
                    AgentTemplatePlural: "Store Roles",
                    TaskSingular: "Order",
                    TaskPlural: "Orders",
                    WorkflowSingular: "Store Process",
                    WorkflowPlural: "Store Processes",
                    SwarmSingular: "Store Team",
                    SwarmPlural: "Store Teams",
                    OutputPlural: "Fulfillment Results",
                    ProviderPlural: "AI Services",
                    OrchestratorTitle: "Store Dispatch"),
                Navigation: new DomainNavigationSection(
                    Overview: "Store Overview",
                    Workforce: "Store Staff",
                    Operations: "Fulfillment",
                    Configuration: "Store Setup"),
                Status: new DomainStatusTerms(
                    Healthy: "On Track",
                    Issues: "issues",
                    Online: "working"),
                PageTitles: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["/"] = "Store Overview",
                    ["/agents"] = "Store Staff",
                    ["/agents/create"] = "Add Store Staff",
                    ["/tasks"] = "Order Queue",
                    ["/workflows"] = "Store Processes",
                    ["/templates"] = "Store Roles",
                    ["/swarms"] = "Store Teams",
                    ["/providers"] = "AI Services",
                    ["/outputs"] = "Fulfillment Results",
                    ["/logs"] = "Store Activity",
                    ["/mcp"] = "Connected Systems"
                })
        };

    public DomainPresentationService(IOptions<DomainPresentationOptions> options)
    {
        var packId = options.Value.ActivePack;
        ActivePack = ResolvePack(packId);
    }

    public DomainPack ActivePack { get; }

    public DomainTerms Terms => ActivePack.Terms;

    public DomainNavigationSection Navigation => ActivePack.Navigation;

    public DomainStatusTerms Status => ActivePack.Status;

    public string ResolvePageTitle(string uri)
    {
        var path = NormalizePath(uri);

        if (path.Contains("/agents/create", StringComparison.OrdinalIgnoreCase))
            return ActivePack.PageTitles["/agents/create"];
        if (path.Contains("/agents/", StringComparison.OrdinalIgnoreCase))
            return $"{Terms.AgentSingular} Profile";

        foreach (var pair in ActivePack.PageTitles.OrderByDescending(p => p.Key.Length))
        {
            if (path.Equals(pair.Key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return ActivePack.PageTitles["/"];
    }

    private static DomainPack ResolvePack(string? packId)
        => !string.IsNullOrWhiteSpace(packId) && Packs.TryGetValue(packId, out var pack)
            ? pack
            : Packs["DefaultMissionControl"];

    private static string NormalizePath(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var absolute))
            return NormalizePath(absolute.AbsolutePath);

        if (string.IsNullOrWhiteSpace(uri))
            return "/";

        var normalized = uri.Trim();
        normalized = normalized.TrimEnd('/');

        return string.IsNullOrWhiteSpace(normalized) ? "/" : normalized;
    }
}
