using Aria.Agents.Memory;
using Aria.Agents.Models;
using Aria.Agents.Runtime;
using Aria.Agents.Tools;
using Aria.Domain.Contracts;
using Aria.Infrastructure.Persistence;
using Aria.Infrastructure.Retrieval;
using Aria.Shared.Telemetry;
using Microsoft.Extensions.Logging;

namespace Aria.Agents.Agents;

/// <summary>An answer with its sources resolved for rendering, plus anything the guardrails removed.</summary>
public sealed record ChartAnswer(
    IReadOnlyList<ChartClaim> Claims,
    bool InsufficientEvidence,
    IReadOnlyList<string> Interventions,
    string ScopeStatement);

public sealed record ChartClaim(string Text, IReadOnlyList<ChartSource> Sources);
public sealed record ChartSource(string Id, string Title, string? Citation);

/// <summary>
/// "Ask this chart" (wireframe S-05).
///
/// Two things make this defensible rather than dangerous. First, retrieval is bound to one
/// patient by the tool layer, and the model cannot widen it. Second, every claim that survives to
/// render carries a citation that resolves to a document actually retrieved this turn — the
/// output guard deletes anything else, including a plausible-looking fabricated id.
///
/// The scope statement is returned with the answer, not buried in settings, because the user
/// needs to know what the answer is drawn from at the moment they read it.
/// </summary>
public sealed class ChartQaService(
    GuardedAgentRunner runner,
    AriaDbContext db,
    ClinicalToolFactory toolFactory,
    ISearchIndex search,
    IAriaEventSink events,
    ILogger<ChartQaService> logger)
{
    private const string Scope =
        "Answers are drawn only from this patient's signed record. Always verify before acting.";

    public async Task<ChartAnswer> AskAsync(
        AgentContext context, string patientId, string question, CancellationToken ct = default)
    {
        var scoped = context with { PatientId = patientId };

        // Pre-retrieve so the shield can inspect the retrieved text BEFORE the model sees it.
        // Prior notes are untrusted: an injected string persisted months ago is a real attack path.
        var prefetched = await search.SearchPatientRecordAsync(question, context.TenantId, patientId, 6, ct);

        var result = await runner.RunAsync<CitedAnswer>(
            agentId: AgentIds.ChartQa,
            context: scoped,
            promptId: AgentIds.ChartQa,
            userMessage: question,
            task: ModelTask.ChartQa,
            tools: toolFactory.ForChartQa(scoped),
            contextProviders: [new PatientContextProvider(db, scoped)],
            untrustedInputs: prefetched,
            enforceOutput: (answer, scope) => runner.Guards.EnforceCitations(answer, scope),
            ct: ct);

        events.Emit(AriaEvents.SuggestionShown, new Dictionary<string, object?>
        {
            ["surface"] = "chart_qa", ["patient_id"] = patientId,
        });

        if (!result.Allowed || result.Value is null)
        {
            logger.LogWarning("Chart Q&A unavailable: {Reason}", result.DenialReason);
            return new ChartAnswer([], true, result.Interventions, Scope);
        }

        // Resolve each surviving citation to something renderable. A citation the UI cannot open
        // is not a citation, so anything unresolvable is dropped here too.
        var byId = prefetched.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var claims = new List<ChartClaim>();

        foreach (var claim in result.Value.Claims)
        {
            var sources = claim.SourceIds
                .Where(byId.ContainsKey)
                .Select(id => new ChartSource(id, byId[id].Title, byId[id].Citation))
                .ToList();

            if (sources.Count > 0) claims.Add(new ChartClaim(claim.Text, sources));
        }

        return new ChartAnswer(
            claims,
            result.Value.InsufficientEvidence || claims.Count == 0,
            result.Interventions,
            Scope);
    }
}
