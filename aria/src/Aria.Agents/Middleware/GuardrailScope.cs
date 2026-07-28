using Aria.Domain;
using Aria.Domain.Contracts;

namespace Aria.Agents.Middleware;

/// <summary>
/// Per-run guardrail state, flowed through the async call chain.
///
/// It carries two things the tool layer needs but the model must never see: which untrusted
/// content is currently in context, and the running list of interventions to report back.
/// </summary>
public sealed class GuardrailScope
{
    private static readonly AsyncLocal<GuardrailScope?> Ambient = new();

    public static GuardrailScope? Current => Ambient.Value;

    public static IDisposable Begin(GuardrailScope scope)
    {
        Ambient.Value = scope;
        return new Pop();
    }

    public required Runtime.AgentContext Context { get; init; }
    public required string AgentId { get; init; }

    /// <summary>Documents in context this turn, with their trust level.</summary>
    public List<RetrievedDocument> Documents { get; } = [];

    /// <summary>
    /// True when any content the model can see this turn was authored outside our trust boundary.
    /// While true, Draft/Hold/Commit tool calls are refused (plan.md §7, D3).
    /// </summary>
    public bool HasUntrustedContent => Documents.Any(d => d.Trust != TrustLevel.Trusted);

    /// <summary>Ids the shield removed. Kept so the UI can honestly say what was dropped.</summary>
    public List<string> QuarantinedDocumentIds { get; } = [];

    public List<string> Interventions { get; } = [];

    public void Intervene(string reason, string? detail = null) =>
        Interventions.Add(detail is null ? reason : $"{reason}: {detail}");

    /// <summary>Citation ids the tools actually returned this turn. Anything else is a hallucination.</summary>
    public HashSet<string> ResolvableCitationIds { get; } = new(StringComparer.Ordinal);

    private sealed class Pop : IDisposable
    {
        public void Dispose() => Ambient.Value = null;
    }
}
