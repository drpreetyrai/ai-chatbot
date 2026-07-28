using Aria.Domain.Contracts;
using Aria.Shared.Telemetry;
using Microsoft.Extensions.Logging;

namespace Aria.Agents.Middleware;

/// <summary>
/// Output enforcement, applied to the typed result after the model is done.
///
/// The operative verb throughout is DELETE, not flag. A prompt that says "always cite your
/// sources" is a hope; code that removes an uncited claim before it can be rendered is a
/// guarantee. If enforcement empties the result, the surface says so plainly rather than
/// showing something plausible.
/// </summary>
public sealed class OutputGuards(IAriaEventSink events, ILogger<OutputGuards> logger)
{
    /// <summary>
    /// Drops claims with no citation, and claims citing an id no tool actually returned this turn.
    /// The second case is the one that matters: a fabricated citation is worse than no citation,
    /// because it looks like diligence.
    /// </summary>
    public CitedAnswer EnforceCitations(CitedAnswer answer, GuardrailScope scope)
    {
        var kept = new List<Claim>();

        foreach (var claim in answer.Claims)
        {
            if (claim.SourceIds.Count == 0)
            {
                scope.Intervene(GuardrailReason.CitationMissing, Trim(claim.Text));
                events.Emit(AriaEvents.GuardrailPrefix + GuardrailReason.CitationMissing);
                logger.LogWarning("Dropped uncited claim: {Text}", Trim(claim.Text));
                continue;
            }

            var resolvable = claim.SourceIds.Where(scope.ResolvableCitationIds.Contains).ToList();

            if (resolvable.Count == 0)
            {
                scope.Intervene(GuardrailReason.CitationUnresolvable, string.Join(",", claim.SourceIds));
                events.Emit(AriaEvents.GuardrailPrefix + GuardrailReason.CitationUnresolvable);
                logger.LogError("Dropped claim citing unresolvable source(s): {Ids}", string.Join(",", claim.SourceIds));
                continue;
            }

            kept.Add(new Claim { Text = claim.Text, SourceIds = resolvable });
        }

        return new CitedAnswer
        {
            Claims = kept,
            // "Showing nothing rather than guessing" is the honest outcome, and it is a state
            // the UI is built to render (wireframe §10).
            InsufficientEvidence = answer.InsufficientEvidence || kept.Count == 0,
        };
    }

    /// <summary>
    /// Removes considerations whose citation does not resolve to a section in the pinned pack.
    /// No citation → the item is not shown. That is the hard rule that makes the evidence drawer
    /// defensible rather than a liability (wireframe S-08).
    /// </summary>
    public RankedConsiderations EnforceCitations(RankedConsiderations input, GuardrailScope scope)
    {
        var kept = new List<Consideration>();

        foreach (var c in input.Considerations)
        {
            if (string.IsNullOrWhiteSpace(c.CitationId) || !scope.ResolvableCitationIds.Contains(c.CitationId))
            {
                scope.Intervene(GuardrailReason.CitationUnresolvable, $"{c.Title} -> '{c.CitationId}'");
                events.Emit(AriaEvents.GuardrailPrefix + GuardrailReason.CitationUnresolvable);
                logger.LogError("Dropped consideration '{Title}' citing '{Citation}'.", c.Title, c.CitationId);
                continue;
            }
            kept.Add(c);
        }

        return new RankedConsiderations
        {
            Considerations = [.. kept.OrderByDescending(c => c.Strength)],
            SafetyChecks = input.SafetyChecks,
        };
    }

    /// <summary>
    /// Every sentence must trace to a transcript window. A span without provenance is removed,
    /// because the product's central promise is that you can always ask "where did that come from?"
    /// and get an answer.
    /// </summary>
    public DraftNoteResult EnforceProvenance(DraftNoteResult note, GuardrailScope scope, long transcriptEndMs)
    {
        List<DraftSpan> Clean(List<DraftSpan> spans, string section)
        {
            var kept = new List<DraftSpan>();
            foreach (var s in spans)
            {
                var hasWindow = s.EndMs > s.StartMs;
                var inRange = s.StartMs >= 0 && s.EndMs <= transcriptEndMs + 10_000;

                if (!hasWindow || !inRange)
                {
                    scope.Intervene(GuardrailReason.ProvenanceMissing, $"{section}: {Trim(s.Text)}");
                    events.Emit(AriaEvents.GuardrailPrefix + GuardrailReason.ProvenanceMissing);
                    logger.LogWarning("Dropped span without usable provenance in {Section}: {Text}", section, Trim(s.Text));
                    continue;
                }
                kept.Add(s);
            }
            return kept;
        }

        return new DraftNoteResult
        {
            Subjective = Clean(note.Subjective, "subjective"),
            Objective  = Clean(note.Objective,  "objective"),
            Assessment = Clean(note.Assessment, "assessment"),
            Plan       = Clean(note.Plan,       "plan"),
            Codes      = note.Codes,
        };
    }

    /// <summary>
    /// Lexical groundedness: what fraction of a claim's substantive words appear in its sources.
    ///
    /// This is a screen, not a verdict — the real service does this far better and replaces it
    /// when configured. Its job here is to make the degraded path real rather than theoretical:
    /// below threshold, the span is demoted to low confidence so the clinician is forced to look.
    /// </summary>
    public double EstimateGroundedness(string claim, IEnumerable<string> sources)
    {
        var claimTokens = Tokens(claim);
        if (claimTokens.Count == 0) return 1.0;

        var sourceTokens = sources.SelectMany(Tokens).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sourceTokens.Count == 0) return 0.0;

        var supported = claimTokens.Count(t => sourceTokens.Contains(t));
        return Math.Round((double)supported / claimTokens.Count, 3);
    }

    private static List<string> Tokens(string text) =>
        [.. text.ToLowerInvariant()
             .Split([' ', ',', '.', ';', ':', '(', ')', '·', '—', '-', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
             .Where(t => t.Length > 3)];

    private static string Trim(string s) => s.Length <= 70 ? s : string.Concat(s.AsSpan(0, 69), "…");
}
