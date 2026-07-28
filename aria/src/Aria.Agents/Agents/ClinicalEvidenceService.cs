using Aria.Agents.Memory;
using Aria.Agents.Models;
using Aria.Agents.Runtime;
using Aria.Agents.Tools;
using Aria.Domain.Contracts;
using Aria.Infrastructure.Persistence;
using Aria.Infrastructure.Retrieval;
using Aria.Safety;
using Aria.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aria.Agents.Agents;

public sealed record EvidenceItem(
    string Title, int Strength, string Suggested, string CitationId, string Citation, string? Url);

public sealed record EvidenceResult(
    IReadOnlyList<EvidenceItem> Considerations,
    IReadOnlyList<string> SafetyChecks,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> Interventions,
    string Disclaimer)
{
    /// <summary>True when enforcement left nothing. The UI renders this as an honest empty state.</summary>
    public bool NothingCited => Considerations.Count == 0;
}

/// <summary>
/// The clinical support drawer (wireframe S-08): ranked considerations, every one cited, and
/// safety checks kept separate from the differential because they are a different cognitive task.
///
/// This is the highest-risk surface in the product, so it is the one where the guardrail does the
/// most work: an item whose citation does not resolve to a section in the tenant's pinned pack is
/// deleted before render. If that empties the list, the drawer says so.
/// </summary>
public sealed class ClinicalEvidenceService(
    GuardedAgentRunner runner,
    AriaDbContext db,
    ClinicalToolFactory toolFactory,
    ISearchIndex search,
    AllergyConflictChecker allergyChecker,
    IAriaEventSink events,
    ILogger<ClinicalEvidenceService> logger)
{
    private const string Disclaimer = "Decision support only. The treating clinician decides.";

    public async Task<EvidenceResult> ConsiderAsync(
        AgentContext context, IReadOnlyList<string> findings, CancellationToken ct = default)
    {
        if (findings.Count == 0)
            return new EvidenceResult([], [], findings, [], Disclaimer);

        var query = string.Join(", ", findings);

        // Pre-retrieve so the citation set is known before the model answers.
        var guidelines = await search.SearchGuidelinesAsync(query, context.GuidelinePackVersion, null, 6, ct);

        var result = await runner.RunAsync<RankedConsiderations>(
            agentId: AgentIds.ClinicalEvidence,
            context: context,
            promptId: AgentIds.ClinicalEvidence,
            userMessage: $"Findings under consideration: {query}\n\n" +
                         "Return ranked considerations, each citing a guideline id you retrieved.",
            task: ModelTask.ClinicalEvidence,
            tools: toolFactory.ForClinicalEvidence(context),
            contextProviders: context.PatientId is null ? null : [new PatientContextProvider(db, context)],
            untrustedInputs: guidelines,
            enforceOutput: (r, scope) => runner.Guards.EnforceCitations(r, scope),
            ct: ct);

        events.Emit(AriaEvents.SuggestionShown, new Dictionary<string, object?> { ["surface"] = "clinical_evidence" });

        if (!result.Allowed || result.Value is null)
        {
            logger.LogWarning("Clinical evidence unavailable: {Reason}", result.DenialReason);
            return new EvidenceResult([], [], findings, result.Interventions, Disclaimer);
        }

        var byId = guidelines.ToDictionary(g => g.Id, StringComparer.Ordinal);

        var items = result.Value.Considerations
            .Where(c => byId.ContainsKey(c.CitationId))
            .Select(c => new EvidenceItem(
                c.Title, Math.Clamp(c.Strength, 1, 5), c.Suggested, c.CitationId,
                byId[c.CitationId].Citation ?? c.CitationId, byId[c.CitationId].Url))
            .ToList();

        // Deterministic safety checks are appended to whatever the model produced. They are not
        // subject to its judgement, and they are the reason this drawer can be trusted at all.
        var safetyChecks = result.Value.SafetyChecks.ToList();
        safetyChecks.AddRange(await DeterministicSafetyChecksAsync(context, findings, ct));

        if (items.Count == 0)
            logger.LogInformation("All considerations removed by citation enforcement — showing nothing rather than guessing.");

        return new EvidenceResult(items, safetyChecks, findings, result.Interventions, Disclaimer);
    }

    /// <summary>
    /// Checks that must appear whether or not a model thought of them. Allergy contraindications
    /// belong here, not in a prompt.
    /// </summary>
    private async Task<List<string>> DeterministicSafetyChecksAsync(
        AgentContext context, IReadOnlyList<string> findings, CancellationToken ct)
    {
        var checks = new List<string>();
        if (context.PatientId is null) return checks;

        var flags = await db.PatientFlags.AsNoTracking()
            .Where(f => f.PatientId == context.PatientId)
            .ToListAsync(ct);

        var conflicts = allergyChecker.Check(flags, findings);
        checks.AddRange(conflicts.Select(c =>
            $"{c.AllergyLabel} — {c.DrugLabel} is contraindicated. {c.Explanation}"));

        return checks;
    }
}
