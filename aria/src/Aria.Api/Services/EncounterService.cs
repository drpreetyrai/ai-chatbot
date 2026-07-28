using Aria.Domain;
using Aria.Domain.Encounters;
using Aria.Infrastructure.Audit;
using Aria.Infrastructure.Persistence;
using Aria.Infrastructure.Seed;
using Aria.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Aria.Api.Services;

/// <summary>
/// Drives the encounter lifecycle and the transcript feed.
///
/// When Azure AI Speech is configured, segments arrive from streaming ASR. When it is not, the
/// scripted consultation is played through the same pipeline at realistic pace. That is Demo Mode
/// (plan.md §14.1) and it is deliberately the same code path: the onboarding a clinician sees,
/// the demo a hospital is shown, and the smoke test CI runs are one thing, so none of them can
/// quietly rot.
/// </summary>
public sealed class EncounterService(
    AriaDbContext db,
    IAuditService audit,
    IAriaEventSink events,
    Aria.Shared.Configuration.AriaOptions options,
    ILogger<EncounterService> logger)
{
    public async Task<Consent> CaptureConsentAsync(
        ClinicianIdentity identity, string encounterId, bool granted, CancellationToken ct = default)
    {
        var encounter = await db.Encounters.FirstAsync(e => e.Id == encounterId, ct);

        var consent = new Consent
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            EncounterId = encounterId,
            CapturedBy = identity.DoctorId,
            CapturedAt = DateTimeOffset.UtcNow,
            Granted = granted,
        };

        db.Consents.Add(consent);
        encounter.ConsentId = consent.Id;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(identity.TenantId, identity.DoctorId, ActorKind.Clinician,
            granted ? AuditActions.ConsentCaptured : AuditActions.ConsentDeclined,
            "encounter", encounterId, encounter.PatientId, ct: ct);

        // Declined consent is a supported outcome, not an error. The clinician still works —
        // manually. Refusing to capture must never mean refusing to function.
        if (!granted)
            logger.LogInformation("Consent declined for {EncounterId}. Ambient capture disabled; manual documentation available.",
                encounterId);

        return consent;
    }

    public async Task<Encounter> StartAsync(
        ClinicianIdentity identity, string encounterId, CancellationToken ct = default)
    {
        var encounter = await db.Encounters.FirstAsync(e => e.Id == encounterId, ct);

        var consent = encounter.ConsentId is null
            ? null
            : await db.Consents.FirstOrDefaultAsync(c => c.Id == encounter.ConsentId, ct);

        // The state machine refuses to start recording without granted consent. That rule lives
        // in the domain, not in this method, so it cannot be bypassed by a different caller.
        EncounterStateMachine.Transition(encounter, EncounterState.Recording, consent?.Granted ?? false);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(identity.TenantId, identity.DoctorId, ActorKind.Clinician,
            AuditActions.EncounterStarted, "encounter", encounterId, encounter.PatientId, ct: ct);

        events.Emit(AriaEvents.EncounterStarted, new Dictionary<string, object?>
        {
            ["encounter_id"] = encounterId,
            ["patient_id"] = encounter.PatientId,
            ["doctor_id"] = identity.DoctorId,
        });

        return encounter;
    }

    public async Task<Encounter> EndAsync(
        ClinicianIdentity identity, string encounterId, CancellationToken ct = default)
    {
        var encounter = await db.Encounters.FirstAsync(e => e.Id == encounterId, ct);

        if (encounter.State is EncounterState.Recording or EncounterState.Paused)
            EncounterStateMachine.Transition(encounter, EncounterState.Ended, true);

        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(identity.TenantId, identity.DoctorId, ActorKind.Clinician,
            AuditActions.EncounterEnded, "encounter", encounterId, encounter.PatientId,
            detail: new { durationSeconds = Math.Round(encounter.Duration.TotalSeconds) }, ct: ct);

        events.Emit(AriaEvents.EncounterEnded, new Dictionary<string, object?>
        {
            ["encounter_id"] = encounterId,
            ["duration_s"] = Math.Round(encounter.Duration.TotalSeconds),
        });

        return encounter;
    }

    /// <summary>
    /// Streams the scripted consultation at realistic pace, persisting each segment as it lands.
    ///
    /// Persisting during playback matters: it means the transcript, the live extraction and the
    /// eventual draft all read from the same table they would in production, so nothing about the
    /// downstream pipeline is special-cased for the demo.
    /// </summary>
    public async IAsyncEnumerable<TranscriptSegment> PlayDemoTranscriptAsync(
        string encounterId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var existing = await db.TranscriptSegments
            .Where(s => s.EncounterId == encounterId)
            .ToListAsync(ct);

        if (existing.Count > 0)
        {
            // Replaying an encounter that already has a transcript returns it instantly rather
            // than duplicating rows or making the user wait through the script again.
            foreach (var s in existing.OrderBy(s => s.StartMs)) yield return s;
            yield break;
        }

        var lines = DemoEncounterScript.Lines;
        long previousEnd = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            var speed = Math.Max(0.1, options.DemoPlaybackSpeed);
            var gapMs = (line.StartMs - previousEnd) / speed;
            var speakMs = (line.EndMs - line.StartMs) / speed;
            previousEnd = line.EndMs;

            // Clamped so a realistic demo never stalls, and a fast one never busy-loops.
            var delay = Math.Clamp(gapMs + speakMs, 0, 3_000);
            if (delay >= 1) await Task.Delay(TimeSpan.FromMilliseconds(delay), ct);

            var segment = new TranscriptSegment
            {
                Id = $"{encounterId}-seg-{i:D3}",
                EncounterId = encounterId,
                Speaker = line.Speaker,
                Text = line.Text,
                StartMs = line.StartMs,
                EndMs = line.EndMs,
                Confidence = line.Confidence,
                IsFinal = true,
            };

            db.TranscriptSegments.Add(segment);
            await db.SaveChangesAsync(ct);

            yield return segment;
        }
    }

    public async Task MarkMomentAsync(string encounterId, long offsetMs, CancellationToken ct = default)
    {
        var encounter = await db.Encounters.FirstAsync(e => e.Id == encounterId, ct);
        encounter.MarkedMomentsMs.Add(offsetMs);
        await db.SaveChangesAsync(ct);
    }
}
