using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public sealed class LocalCapabilityDraftValidator
{
    public LocalCapabilityDraft Validate(LocalCapabilityDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            throw new InvalidOperationException("Capability draft requires a name.");
        }

        if (string.IsNullOrWhiteSpace(draft.Description))
        {
            throw new InvalidOperationException("Capability draft requires a description.");
        }

        if (string.IsNullOrWhiteSpace(draft.Script))
        {
            throw new InvalidOperationException("Capability draft requires a script.");
        }

        if (!string.Equals(draft.ExecutionType, nameof(LocalCapabilityExecutionType.PowerShell), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("V1 generated capabilities must use PowerShell execution.");
        }

        return draft;
    }
}
