using Aria.Agents.Models;
using Aria.Agents.Runtime;
using Aria.Agents.Tools;
using Aria.Domain;
using Aria.Domain.Contracts;
using Aria.Infrastructure.Persistence;
using Aria.Safety;
using Aria.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aria.Agents.Agents;

/// <summary>Live extraction plus any allergy conflicts detected mid-conversation.</summary>
public sealed record LiveExtraction(
    ExtractedEntities Entities,
    IReadOnlyList<AllergyConflict> Conflicts,
    bool Degraded);

/// <summary>
/// Runs during the consultation, on a rolling window rather than the whole transcript, so latency
/// stays flat as an encounter runs long.
///
/// The part that earns its place is the allergy check: it fires the instant a drug is mentioned,
/// while the patient is still in the room. Catching a contraindication then is worth more than a
/// perfect note afterwards (wireframe S-03).
/// </summary>
public sealed class ExtractionService(
    GuardedAgentRunner runner,
    AriaDbContext db,
    AllergyConflictChecker allergyChecker,
    IAriaEventSink events,
    ILogger<ExtractionService> logger)
{
    /// <summary>How far back each pass looks. Keeps the prompt small and the latency predictable.</summary>
    private const long WindowMs = 45_000;

    public async Task<LiveExtraction> ExtractAsync(
        AgentContext context, string encounterId, long uptoMs, CancellationToken ct = default)
    {
        var segments = await db.TranscriptSegments.AsNoTracking()
            .Where(s => s.EncounterId == encounterId && s.IsFinal && s.EndMs <= uptoMs)
            .OrderBy(s => s.StartMs)
            .ToListAsync(ct);

        if (segments.Count == 0)
            return new LiveExtraction(new ExtractedEntities(), [], false);

        // Clamp to the transcript we actually have. A caller asking for "everything so far" passes
        // long.MaxValue, and subtracting the window from that overflows into a start offset far
        // beyond the recording — which silently returns nothing at the exact moment the caller
        // wanted the most.
        var transcriptEnd = segments[^1].EndMs;
        var effectiveUpto = Math.Min(uptoMs, transcriptEnd);

        var windowStart = Math.Max(0, effectiveUpto - WindowMs);
        var window = segments.Where(s => s.EndMs >= windowStart).ToList();

        var transcript = string.Join("\n", window.Select(s => $"[{s.StartMs}] {s.Speaker} {s.Text}"));

        // Scanned from the raw window before the model is asked anything, because the
        // allergy check is not allowed to depend on the model answering. See below.
        var scannedDrugs = allergyChecker.ScanForDrugs(transcript);

        // ── The in-conversation safety check. Deterministic, and it does not wait for the note. ──
        var flags = context.PatientId is null
            ? []
            : await db.PatientFlags.AsNoTracking()
                .Where(f => f.PatientId == context.PatientId)
                .ToListAsync(ct);

        var result = await runner.RunAsync<ExtractedEntities>(
            agentId: AgentIds.Extraction,
            context: context,
            promptId: AgentIds.Extraction,
            userMessage: $"Extract entities from this window of the consultation.\n\n{transcript}",
            task: ModelTask.Extraction,
            ct: ct);

        // Degraded extraction is survivable — the transcript is still on screen — but the
        // allergy check is NOT allowed to degrade with it.
        //
        // This was a real defect: with a live model the extraction agent overran its budget,
        // returned nothing, and the contraindication alert silently stopped firing. An empty
        // entity list conflicts with nothing, so the screen looked calm. The rule this
        // codebase is built on is that the safety-critical path never depends on a
        // probabilistic system — so it runs on the scan, whatever the model did.
        if (!result.Allowed || result.Value is null)
        {
            logger.LogWarning("Live extraction degraded for {EncounterId}: {Reason}. " +
                              "Allergy check continues on the deterministic scan.",
                encounterId, result.DenialReason);

            var degradedConflicts = Report(allergyChecker.Check(flags, scannedDrugs));
            return new LiveExtraction(new ExtractedEntities(), degradedConflicts, true);
        }

        var entities = result.Value;

        // Union, not replacement: the model may name a drug the scanner does not know, and
        // the scanner catches one the model missed. Neither gets to suppress the other.
        var mentionedDrugs = entities.Medications.Select(m => m.Label)
            .Concat(scannedDrugs)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var conflicts = allergyChecker.Check(flags, mentionedDrugs);

        return new LiveExtraction(entities, Report(conflicts), false);

        // Local so both paths — degraded and healthy — emit identically. A conflict that
        // fires but is not telemetered is a conflict nobody can prove fired.
        IReadOnlyList<AllergyConflict> Report(IReadOnlyList<AllergyConflict> found)
        {
            foreach (var c in found)
            {
                logger.LogWarning("Live allergy conflict in {EncounterId}: {Drug} vs {Allergy}",
                    encounterId, c.DrugLabel, c.AllergyLabel);

                events.Emit(AriaEvents.GuardrailPrefix + GuardrailReason.AllergyConflict, new Dictionary<string, object?>
                {
                    ["encounter_id"] = encounterId,
                    ["drug"] = c.DrugCode,
                    ["allergy"] = c.AllergyCode,
                    ["live"] = true,     // caught during the consultation, not after
                });
            }

            return found;
        }
    }
}
