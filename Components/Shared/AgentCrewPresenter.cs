using System.Globalization;
using System.Text;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Components.Shared;

public static class AgentCrewPresenter
{
    public static AgentCrewProfile Build(Agent agent)
    {
        var memoryPct = agent.MemoryLimitMB > 0
            ? Math.Clamp(agent.MemoryUsageMB / agent.MemoryLimitMB * 100, 0, 100)
            : 0;
        var loadPct = Math.Clamp(Math.Max(agent.CpuUsage, memoryPct), 0, 100);
        var successRate = Math.Clamp(agent.SuccessRate, 0, 100);

        return new AgentCrewProfile(
            Callsign: BuildCallsign(agent),
            AvatarInitials: BuildInitials(agent.Name),
            AvatarRank: BuildRank(agent.Type),
            Title: BuildTitle(agent.Type),
            PresenceLabel: BuildPresenceLabel(agent.Status),
            PresenceTone: BuildPresenceTone(agent.Status),
            PresenceSummary: BuildPresenceSummary(agent, loadPct),
            Mission: BuildMission(agent),
            MissionContext: BuildMissionContext(agent, loadPct),
            ConfidenceLabel: $"{successRate.ToString("F0", CultureInfo.InvariantCulture)}%",
            LoadLabel: $"{loadPct.ToString("F0", CultureInfo.InvariantCulture)}%",
            LastSeenLabel: FormatLastSeen(agent.LastSeen));
    }

    private static string BuildCallsign(Agent agent)
    {
        var prefix = agent.Type switch
        {
            AgentType.DataSync => "SYNC",
            AgentType.Reporting => "ECHO",
            AgentType.Integration => "LINK",
            AgentType.Notification => "PULSE",
            AgentType.Processing => "VECTOR",
            _ => "NEXUS"
        };

        var digits = new string((agent.Id ?? string.Empty).Where(char.IsDigit).ToArray());
        var suffix = digits.Length >= 2 ? digits[^2..] : digits.PadLeft(2, '0');

        if (string.IsNullOrWhiteSpace(suffix))
        {
            var nameSignature = new string(agent.Name.Where(char.IsLetterOrDigit).TakeLast(2).ToArray()).ToUpperInvariant();
            suffix = string.IsNullOrWhiteSpace(nameSignature) ? "01" : nameSignature;
        }

        return $"{prefix}-{suffix}";
    }

    private static string BuildInitials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return "AG";

        if (parts.Length == 1)
            return new string(parts[0].Take(2).ToArray()).ToUpperInvariant();

        var sb = new StringBuilder(2);
        sb.Append(char.ToUpperInvariant(parts[0][0]));
        sb.Append(char.ToUpperInvariant(parts[1][0]));
        return sb.ToString();
    }

    private static string BuildRank(AgentType type) => type switch
    {
        AgentType.DataSync => "DS",
        AgentType.Reporting => "RP",
        AgentType.Integration => "IN",
        AgentType.Notification => "NT",
        AgentType.Processing => "PR",
        _ => "AX"
    };

    private static string BuildTitle(AgentType type) => type switch
    {
        AgentType.DataSync => "Data Synchronization Lead",
        AgentType.Reporting => "Reporting Analyst",
        AgentType.Integration => "Integration Specialist",
        AgentType.Notification => "Notification Coordinator",
        AgentType.Processing => "Processing Controller",
        _ => "Custom Operations Specialist"
    };

    private static string BuildPresenceLabel(AgentStatus status) => status switch
    {
        AgentStatus.Active => "Focused",
        AgentStatus.Idle => "Waiting",
        AgentStatus.Warning => "Recovering",
        AgentStatus.Error => "Escalated",
        AgentStatus.Offline => "Offline",
        _ => "Waiting"
    };

    private static string BuildPresenceTone(AgentStatus status) => status switch
    {
        AgentStatus.Active => "active",
        AgentStatus.Idle => "idle",
        AgentStatus.Warning => "warning",
        AgentStatus.Error => "error",
        AgentStatus.Offline => "offline",
        _ => "idle"
    };

    private static string BuildPresenceSummary(Agent agent, double loadPct) => agent.Status switch
    {
        AgentStatus.Active when loadPct >= 85 => "Running hot but holding mission tempo",
        AgentStatus.Active => "Operating cleanly inside mission thresholds",
        AgentStatus.Idle => "Monitoring the queue for the next handoff",
        AgentStatus.Warning => "Stability degraded, keeping the assignment alive",
        AgentStatus.Error => "Operator intervention recommended before reassignment",
        AgentStatus.Offline => "Heartbeat lost from this station",
        _ => "Awaiting command input"
    };

    private static string BuildMission(Agent agent) => agent.Status switch
    {
        AgentStatus.Idle => "Standing by for the next assignment.",
        AgentStatus.Offline => "Disconnected from the command surface.",
        _ when string.IsNullOrWhiteSpace(agent.CurrentTask) => "Awaiting mission details.",
        _ => ToSentence(agent.CurrentTask)
    };

    private static string BuildMissionContext(Agent agent, double loadPct) => agent.Status switch
    {
        AgentStatus.Active when agent.RequiresApproval => "Human review gate remains armed for risky output.",
        AgentStatus.Active when agent.SpawnEnabled => "This operator can decompose work across child agents.",
        AgentStatus.Active when loadPct >= 80 => "Resource pressure is elevated while the current run stays in motion.",
        AgentStatus.Active => "Telemetry remains stable while this assignment is in flight.",
        AgentStatus.Idle => "Queue, approvals, and live signals are under watch.",
        AgentStatus.Warning => "A dependency or downstream service is slowing execution.",
        AgentStatus.Error => "Escalation path is open and this station should be inspected.",
        AgentStatus.Offline => $"Most recent heartbeat was {FormatLastSeen(agent.LastSeen)}.",
        _ => "Mission context will appear once work is assigned."
    };

    private static string ToSentence(string text)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return "Awaiting mission details.";

        var sentence = char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
        return sentence.EndsWith(".") || sentence.EndsWith("!") || sentence.EndsWith("?")
            ? sentence
            : $"{sentence}.";
    }

    private static string FormatLastSeen(DateTime lastSeen)
    {
        if (lastSeen == default)
            return "recently";

        var delta = DateTime.UtcNow - lastSeen;
        if (delta.TotalSeconds < 90)
            return "just now";
        if (delta.TotalMinutes < 60)
            return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24)
            return $"{(int)delta.TotalHours}h ago";

        return $"{(int)delta.TotalDays}d ago";
    }
}

public sealed record AgentCrewProfile(
    string Callsign,
    string AvatarInitials,
    string AvatarRank,
    string Title,
    string PresenceLabel,
    string PresenceTone,
    string PresenceSummary,
    string Mission,
    string MissionContext,
    string ConfidenceLabel,
    string LoadLabel,
    string LastSeenLabel);
