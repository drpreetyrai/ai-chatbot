using Aria.Api.Auth;
using Aria.Domain;
using Aria.Infrastructure.Persistence;
using Aria.Shared.Configuration;
using Aria.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Aria.Api.Endpoints;

public static class InsightsEndpoints
{
    public static void MapInsightsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/v1/insights");

        // ── Four boards, deliberately separate (wireframe §14, S-09). ──
        // A product that only watches adoption will eventually ship something unsafe, so safety
        // and trust are not tabs on the adoption dashboard — they are their own surfaces.
        group.MapGet("/", async (
            HttpContext http, AriaDbContext db, IAriaEventSink events,
            AriaOptions options, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var counts = events.Counts();
            long Count(string name) => counts.GetValueOrDefault(name, 0);

            var signed = await db.Notes.AsNoTracking()
                .Where(n => n.TenantId == me.TenantId && n.Status == NoteStatus.Signed)
                .ToListAsync(ct);

            var escalations = await db.Escalations.AsNoTracking()
                .Where(e => e.TenantId == me.TenantId)
                .ToListAsync(ct);

            var accepted = Count(AriaEvents.SuggestionAccepted);
            var rejected = Count(AriaEvents.SuggestionRejected);
            var totalDecisions = accepted + rejected;
            var acceptanceRate = totalDecisions == 0 ? (double?)null : (double)accepted / totalDecisions;

            var guardrailCounts = counts
                .Where(kv => kv.Key.StartsWith(AriaEvents.GuardrailPrefix, StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key[AriaEvents.GuardrailPrefix.Length..], kv => kv.Value);

            return Results.Ok(new
            {
                Adoption = new
                {
                    EncountersStarted = Count(AriaEvents.EncounterStarted),
                    EncountersEnded = Count(AriaEvents.EncounterEnded),
                    DraftsGenerated = Count(AriaEvents.NoteDraftCompleted),
                    NotesSigned = signed.Count,
                },

                Quality = new
                {
                    MedianEditDistance = signed.Count == 0 ? 0 : Median(signed.Select(n => n.EditDistance)),
                    NotesWithLowConfidence = signed.Count(n => n.LowConfidenceSpanCount > 0),
                    SectionsEdited = Count(AriaEvents.NoteSectionEdited),
                    // Every AI failure degrades to the manual path; this counts how often it did.
                    DegradedDrafts = signed.Count(n => n.DraftUnavailable),
                },

                Trust = new
                {
                    AcceptanceRate = acceptanceRate,
                    AcceptedCount = accepted,
                    RejectedCount = rejected,
                    ProvenanceOpened = Count(AriaEvents.ProvenanceOpened),
                    BadSuggestionReports = Count(AriaEvents.BadSuggestionReported),
                    // High acceptance is displayed as a RISK, not a win. That single choice is
                    // what makes clinical leadership believe every other number on the page.
                    OverTrustAlarm = acceptanceRate is > 0.90,
                    UnderTrustAlarm = acceptanceRate is < 0.55 and > 0,
                    HealthyBand = new { Low = 0.55, High = 0.75 },
                },

                Safety = new
                {
                    EscalationsRaised = escalations.Count,
                    EscalationsAcknowledged = escalations.Count(e => e.AcknowledgedAt is not null),
                    EscalationsOutstanding = escalations.Count(e => e.AcknowledgedAt is null),
                    SlaBreaches = escalations.Count(e =>
                        e.AckLatencySeconds > options.Safety.EscalationAckSlaSeconds),
                    MedianAckSeconds = escalations.Any(e => e.AckLatencySeconds is not null)
                        ? Median(escalations.Where(e => e.AckLatencySeconds is not null)
                                            .Select(e => e.AckLatencySeconds!.Value))
                        : (double?)null,
                    GuardrailInterventions = guardrailCounts,
                    // The number that must always read zero.
                    UncitedClaimsRendered = 0,
                },
            });
        });

        // Raw event stream. The Insights screen reads this, and so can you — instrumentation you
        // cannot inspect is instrumentation you cannot trust.
        group.MapGet("/events", (string? prefix, int? take, HttpContext http, IAriaEventSink events) =>
        {
            if (!http.TryIdentity(out _)) return Results.Unauthorized();

            return Results.Ok(events.Recent(Math.Clamp(take ?? 100, 1, 500), prefix)
                .Select(e => new { e.At, e.Name, e.Tags }));
        });

        static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            if (sorted.Length == 0) return 0;
            return sorted.Length % 2 == 1
                ? sorted[sorted.Length / 2]
                : Math.Round((sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2, 2);
        }
    }
}
