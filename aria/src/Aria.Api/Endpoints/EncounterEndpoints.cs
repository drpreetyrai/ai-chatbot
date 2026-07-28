using System.Text.Json;
using Aria.Agents.Agents;
using Aria.Agents.Runtime;
using Aria.Api.Auth;
using Aria.Api.Services;
using Aria.Domain;
using Aria.Domain.Encounters;
using Aria.Infrastructure.Audit;
using Aria.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aria.Api.Endpoints;

public static class EncounterEndpoints
{
    /// <summary>
    /// SSE frames are serialised by hand, so they must be told to use the same camelCase the
    /// Results.Ok() pipeline applies. Without this the stream silently disagrees with every
    /// other endpoint and the client breaks on a field name.
    /// </summary>
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    public static void MapEncounterEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/v1/encounters");

        group.MapGet("/today", async (HttpContext http, AriaDbContext db, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var encounters = await db.Encounters.AsNoTracking()
                .Where(e => e.TenantId == me.TenantId && e.DoctorId == me.DoctorId)
                .ToListAsync(ct);

            var patients = await db.Patients.AsNoTracking().Include(p => p.Flags)
                .Where(p => encounters.Select(e => e.PatientId).Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, ct);

            var today = DateOnly.FromDateTime(DateTime.Today);

            return Results.Ok(encounters.Select(e =>
            {
                var p = patients[e.PatientId];
                return new
                {
                    e.Id, State = e.State.ToString(), e.Room, e.ChiefComplaint,
                    Patient = new
                    {
                        p.Id, p.Name, p.Mrn, p.Sex,
                        Age = p.AgeYears(today),
                        // Masked by default. Revealing is a separate, audited action (§9.9).
                        Phone = p.MaskedPhone,
                        Flags = p.Flags.Select(f => new { f.Label, Kind = f.Kind.ToString(), Severity = f.Severity.ToString() }),
                    },
                };
            }));
        });

        // Start a walk-in (wireframe S-02 empty state: "No patients checked in. Start a walk-in →").
        // Creating the encounter is deliberately separate from starting capture: an encounter can
        // exist, and be documented manually, without consent ever being granted.
        group.MapPost("/", async (
            CreateEncounterRequest request, HttpContext http, AriaDbContext db,
            IAuditService audit, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            if (!me.CanSign) return me.Denied("start an encounter");

            var patient = await db.Patients.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == request.PatientId && p.TenantId == me.TenantId, ct);

            if (patient is null) return Results.NotFound(new { error = "Patient not found in this tenant." });

            var encounter = new Domain.Encounters.Encounter
            {
                Id = Guid.NewGuid().ToString("n")[..12],
                TenantId = me.TenantId,
                PatientId = patient.Id,
                DoctorId = me.DoctorId,
                Department = me.Department,
                State = EncounterState.CheckedIn,
                Room = request.Room,
                ChiefComplaint = request.ChiefComplaint,
            };

            db.Encounters.Add(encounter);
            await db.SaveChangesAsync(ct);

            await audit.WriteAsync(me.TenantId, me.DoctorId, ActorKind.Clinician,
                "ENCOUNTER_CREATED", "encounter", encounter.Id, patient.Id, ct: ct);

            return Results.Ok(new { encounter.Id, State = encounter.State.ToString(), PatientId = patient.Id });
        });

        group.MapPost("/{id}/consent", async (
            string id, ConsentRequest request, HttpContext http, EncounterService encounters, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var consent = await encounters.CaptureConsentAsync(me, id, request.Granted, ct);
            return Results.Ok(new
            {
                consent.Id, consent.Granted, consent.CapturedAt, consent.RetentionStatement,
                CapturedBy = me.Name,
            });
        });

        group.MapPost("/{id}/start", async (
            string id, HttpContext http, EncounterService encounters, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            try
            {
                var encounter = await encounters.StartAsync(me, id, ct);
                return Results.Ok(new { encounter.Id, State = encounter.State.ToString(), encounter.StartedAt });
            }
            catch (InvalidOperationException ex)
            {
                // Consent missing, or an illegal transition. Both are 409, both are explained.
                return Results.Conflict(new { error = ex.Message });
            }
        });

        group.MapPost("/{id}/end", async (
            string id, HttpContext http, EncounterService encounters, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            var encounter = await encounters.EndAsync(me, id, ct);
            return Results.Ok(new { encounter.Id, State = encounter.State.ToString(), encounter.EndedAt });
        });

        group.MapPost("/{id}/moments", async (
            string id, MomentRequest request, HttpContext http, EncounterService encounters, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out _)) return Results.Unauthorized();
            await encounters.MarkMomentAsync(id, request.OffsetMs, ct);
            return Results.Ok(new { marked = request.OffsetMs });
        });

        // ── The live transcript, as server-sent events. ──
        // Each segment is persisted as it lands, so extraction and the eventual draft read the
        // same table they would in production. Demo Mode is not a separate pipeline.
        group.MapGet("/{id}/transcript/stream", async (
            string id, HttpContext http, EncounterService encounters, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out _)) { http.Response.StatusCode = 401; return; }

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            await foreach (var segment in encounters.PlayDemoTranscriptAsync(id, ct))
            {
                var payload = JsonSerializer.Serialize(new
                {
                    segment.Id, segment.Speaker, segment.Text,
                    segment.StartMs, segment.EndMs, segment.Confidence,
                }, Wire);

                await http.Response.WriteAsync($"event: segment\ndata: {payload}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }

            await http.Response.WriteAsync("event: complete\ndata: {}\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
        });

        // Live segments from the browser's Speech stream.
        //
        // They land in exactly the same table the scripted consultation writes to, so
        // extraction, the scribe and provenance replay cannot tell the difference — the
        // only thing that changes between real capture and Demo Mode is where the words
        // came from.
        group.MapPost("/{id}/transcript", async (
            string id, LiveSegmentRequest request, HttpContext http,
            AriaDbContext db, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            if (!me.IsClinician) return me.Denied("record an encounter");

            var encounter = await db.Encounters.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == id && e.TenantId == me.TenantId, ct);

            if (encounter is null) return Results.NotFound();

            var segment = new TranscriptSegment
            {
                Id = $"{id}-live-{request.OffsetMs:D9}",
                EncounterId = id,
                Speaker = request.Speaker ?? "—",
                Text = request.Text,
                StartMs = request.OffsetMs,
                EndMs = request.OffsetMs + request.DurationMs,
                Confidence = request.Confidence,
                IsFinal = true,
            };

            // Idempotent on the offset: the recogniser can re-deliver a final result, and
            // a duplicated sentence in the transcript becomes a duplicated claim in the note.
            if (!await db.TranscriptSegments.AnyAsync(t => t.Id == segment.Id, ct))
            {
                db.TranscriptSegments.Add(segment);
                await db.SaveChangesAsync(ct);
            }

            return Results.Ok(new { segment.Id, segment.StartMs, segment.EndMs });
        });

        group.MapGet("/{id}/transcript", async (string id, AriaDbContext db, CancellationToken ct) =>
            Results.Ok(await db.TranscriptSegments.AsNoTracking()
                .Where(s => s.EncounterId == id)
                .OrderBy(s => s.StartMs)
                .Select(s => new { s.Id, s.Speaker, s.Text, s.StartMs, s.EndMs, s.Confidence })
                .ToListAsync(ct)));

        // Everything the encounter screen needs to render itself from cold — after a page
        // reload, or when a clinician opens a consultation someone else started. Without
        // StartedAt the transcript can only show offsets, which are meaningless to anyone
        // reconstructing what happened when.
        group.MapGet("/{id}", async (string id, HttpContext http, AriaDbContext db, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var encounter = await db.Encounters.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == id && e.TenantId == me.TenantId, ct);

            if (encounter is null) return Results.NotFound();
            if (me.GuardPatientAccess(encounter.PatientId) is { } denied) return denied;

            var segments = await db.TranscriptSegments.AsNoTracking()
                .Where(s => s.EncounterId == id)
                .OrderBy(s => s.StartMs)
                .Select(s => new { s.Id, s.Speaker, s.Text, s.StartMs, s.EndMs, s.Confidence })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                encounter.Id,
                encounter.PatientId,
                State = encounter.State.ToString(),
                encounter.StartedAt,
                encounter.EndedAt,
                encounter.ChiefComplaint,
                Segments = segments,
            });
        });

        // ── Correcting who said what. ──
        //
        // Diarisation separates the voices reliably; deciding which one is the clinician is
        // a guess, and this is how a human overrules it. It matters more than it looks:
        // "no chest pain" attributed to the wrong speaker inverts the clinical meaning, and
        // the note is drafted from these labels.
        group.MapPost("/{id}/transcript/swap-speakers", async (
            string id, HttpContext http, AriaDbContext db, IAuditService audit, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            if (!me.IsClinician) return me.Denied("correct a transcript");

            var encounter = await db.Encounters.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == id && e.TenantId == me.TenantId, ct);

            if (encounter is null) return Results.NotFound();

            var segments = await db.TranscriptSegments
                .Where(s => s.EncounterId == id && (s.Speaker == "Dr." || s.Speaker == "Pt."))
                .ToListAsync(ct);

            foreach (var segment in segments)
                segment.Speaker = segment.Speaker == "Dr." ? "Pt." : "Dr.";

            await db.SaveChangesAsync(ct);

            await audit.WriteAsync(me.TenantId, me.DoctorId, ActorKind.Clinician,
                "TRANSCRIPT_SPEAKERS_SWAPPED", "encounter", id, encounter.PatientId,
                detail: new { segments = segments.Count }, ct: ct);

            return Results.Ok(new { swapped = segments.Count });
        });

        // ── Live extraction, plus any allergy conflict caught mid-conversation. ──
        group.MapGet("/{id}/entities", async (
            string id, long? uptoMs, HttpContext http, AriaDbContext db,
            ExtractionService extraction, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var encounter = await db.Encounters.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
            if (encounter is null) return Results.NotFound();

            var context = new AgentContext(me, http.FacilityId(), encounter.PatientId, id);
            var result = await extraction.ExtractAsync(context, id, uptoMs ?? long.MaxValue, ct);

            return Results.Ok(new
            {
                Symptoms = result.Entities.Symptoms,
                Vitals = result.Entities.Vitals,
                Medications = result.Entities.Medications,
                Orders = result.Entities.Orders,
                // The warning that fires while the patient is still in the room.
                Conflicts = result.Conflicts.Select(c => new
                {
                    c.DrugLabel, c.AllergyLabel, Severity = c.Severity.ToString(), c.Explanation,
                }),
                result.Degraded,
            });
        });

        // ── Encounter close → draft. This is where the scribe runs. ──
        group.MapPost("/{id}/draft", async (
            string id, HttpContext http, AriaDbContext db, ScribeService scribe, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var encounter = await db.Encounters.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
            if (encounter is null) return Results.NotFound();

            var existing = await db.Notes.AsNoTracking()
                .FirstOrDefaultAsync(n => n.EncounterId == id && n.Status != Domain.NoteStatus.Discarded, ct);

            if (existing is not null) return Results.Ok(new { noteId = existing.Id, existing = true });

            var context = new AgentContext(me, http.FacilityId(), encounter.PatientId, id);
            var note = await scribe.DraftAsync(context, id, ct);

            return Results.Ok(new { noteId = note.Id, existing = false, degraded = note.DraftUnavailable });
        });
    }
}

public sealed record CreateEncounterRequest(string PatientId, string? ChiefComplaint, string? Room);
public sealed record ConsentRequest(bool Granted);
public sealed record MomentRequest(long OffsetMs);
public sealed record LiveSegmentRequest(string Text, long OffsetMs, long DurationMs, double Confidence, string? Speaker);
