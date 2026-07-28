using Microsoft.Extensions.AI;

namespace Aria.Agents.Models;

/// <summary>
/// Model choice is a per-task decision resolved here, never hard-coded at a call site. That is
/// what makes "a provider outage degrades quality rather than availability" implementable rather
/// than aspirational (plan.md §1.4).
/// </summary>
public enum ModelTask { Extraction, NoteSynthesis, ChartQa, ClinicalEvidence, MessageDraft, Classification }

public interface IModelRouter
{
    IChatClient GetChatClient(ModelTask task);
    TimeSpan TimeoutFor(ModelTask task);
    string DeploymentFor(ModelTask task);

    /// <summary>False when every model call is being served by the deterministic local stub.</summary>
    bool IsLive { get; }

    /// <summary>Shown in the startup banner and on the UI status chip. Never let the operator guess.</summary>
    string ModeDescription { get; }
}
