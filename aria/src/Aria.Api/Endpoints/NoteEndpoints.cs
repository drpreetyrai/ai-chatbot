using Aria.Api.Auth;
using Aria.Api.Services;
using Aria.Domain;
using Aria.Domain.Notes;
using Aria.Infrastructure.Audit;
using Aria.Infrastructure.Persistence;
using Aria.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Aria.Api.Endpoints;

public static class NoteEndpoints
{
    public static void MapNoteEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/v1/notes");

        group.MapGet("/{id}", async (string id, HttpContext http, AriaDbContext db, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var note = await db.Notes.AsNoTracking()
                .Include(n => n.Sections).Include(n => n.AttachedActions)
                .Include(n => n.Codes).Include(n => n.Addenda)
                .FirstOrDefaultAsync(n => n.Id == id && n.TenantId == me.TenantId, ct);

            if (note is null) return Results.NotFound();

            var patient = await db.Patients.AsNoTracking().Include(p => p.Flags)
                .FirstAsync(p => p.Id == note.PatientId, ct);

            note.IsSignable(out var blocker);

            return Results.Ok(new
            {
                note.Id, note.EncounterId, Status = note.Status.ToString(), note.TemplateId,
                note.ModelVersion, note.PromptVersion, note.EditDistance, note.DraftUnavailable,
                note.DraftCreatedAt, note.SignedAt, note.SignedBy,
                note.LowConfidenceSpanCount,
                Signable = blocker is null,
                Blocker = blocker,
                Patient = new
                {
                    patient.Id, patient.Name, patient.Mrn, patient.Sex,
                    Age = patient.AgeYears(DateOnly.FromDateTime(DateTime.Today)),
                    Flags = patient.Flags.Select(f => new { f.Label, Kind = f.Kind.ToString(), Severity = f.Severity.ToString() }),
                },
                Sections = note.Sections.OrderBy(s => s.Kind).Select(s => new
                {
                    s.Id, Kind = s.Kind.ToString(),
                    Spans = s.Spans.OrderBy(sp => sp.Ordinal).Select(sp => new
                    {
                        sp.Id, sp.Text, sp.Confidence,
                        Band = sp.Band.ToString(),
                        sp.TranscriptStartMs, sp.TranscriptEndMs,
                        sp.AcceptedByHuman, sp.EditedByHuman, sp.FlagReason,
                        sp.HasProvenance,
                    }),
                }),
                AttachedActions = note.AttachedActions.Select(a => new
                {
                    a.Id, Kind = a.Kind.ToString(), a.Description, a.Enabled, a.BlockedReason,
                }),
                Codes = note.Codes.Select(c => new { c.Code, c.System, c.Display, c.Confidence }),
                Addenda = note.Addenda.Select(a => new { a.Id, a.Body, a.CreatedAt, a.AuthorId }),
            });
        });

        // ── Editing a span. Only ever on a draft; a signed note takes addenda instead. ──
        group.MapPatch("/{id}/spans/{spanId}", async (
            string id, string spanId, EditSpanRequest request, HttpContext http,
            AriaDbContext db, IAuditService audit, IAriaEventSink events, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var note = await db.Notes.Include(n => n.Sections)
                .FirstOrDefaultAsync(n => n.Id == id && n.TenantId == me.TenantId, ct);

            if (note is null) return Results.NotFound();
            if (note.Status is NoteStatus.Signed)
                return Results.Conflict(new { error = "This note is signed and immutable. Add an addendum instead." });

            var span = note.Sections.SelectMany(s => s.Spans).FirstOrDefault(sp => sp.Id == spanId);
            if (span is null) return Results.NotFound();

            span.Text = request.Text;
            span.EditedByHuman = true;
            span.AcceptedByHuman = true;    // editing it is a stronger form of accepting it
            await db.SaveChangesAsync(ct);

            await audit.WriteAsync(me.TenantId, me.DoctorId, ActorKind.Clinician,
                AuditActions.NoteEdited, "span", spanId, note.PatientId, ct: ct);

            // Which sections the model is worst at → the next eval target (wireframe §14).
            events.Emit(AriaEvents.NoteSectionEdited, new Dictionary<string, object?>
            {
                ["note_id"] = id,
                ["section"] = note.Sections.First(s => s.Spans.Any(sp => sp.Id == spanId)).Kind.ToString(),
            });

            return Results.Ok(new { span.Id, span.Text, span.EditedByHuman });
        });

        // ── Accept / reject a low-confidence span. This is the gate on signing. ──
        group.MapPost("/{id}/spans/{spanId}/{decision}", async (
            string id, string spanId, string decision, HttpContext http,
            AriaDbContext db, IAuditService audit, IAriaEventSink events, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            if (decision is not ("accept" or "reject")) return Results.BadRequest(new { error = "Use accept or reject." });

            var note = await db.Notes.Include(n => n.Sections)
                .FirstOrDefaultAsync(n => n.Id == id && n.TenantId == me.TenantId, ct);

            if (note is null) return Results.NotFound();
            if (note.Status is NoteStatus.Signed) return Results.Conflict(new { error = "Note is signed." });

            var section = note.Sections.FirstOrDefault(s => s.Spans.Any(sp => sp.Id == spanId));
            var span = section?.Spans.FirstOrDefault(sp => sp.Id == spanId);
            if (span is null || section is null) return Results.NotFound();

            if (decision is "accept")
            {
                span.AcceptedByHuman = true;
            }
            else
            {
                // Rejecting removes the claim outright. A rejected AI claim must not survive in
                // the record in any form — and it becomes an eval candidate.
                section.Spans.Remove(span);
            }

            await db.SaveChangesAsync(ct);

            await audit.WriteAsync(me.TenantId, me.DoctorId, ActorKind.Clinician,
                decision is "accept" ? AuditActions.SpanAccepted : AuditActions.SpanRejected,
                "span", spanId, note.PatientId, note.ModelVersion, note.PromptVersion,
                detail: new { text = span.Text, confidence = span.Confidence }, ct: ct);

            events.Emit(decision is "accept" ? AriaEvents.SuggestionAccepted : AriaEvents.SuggestionRejected,
                new Dictionary<string, object?>
                {
                    ["note_id"] = id, ["surface"] = "note_span",
                    ["confidence"] = span.Confidence, ["band"] = span.Band.ToString(),
                });

            note.IsSignable(out var blocker);
            return Results.Ok(new { spanId, decision, remainingLowConfidence = note.LowConfidenceSpanCount, blocker });
        });

        group.MapPatch("/{id}/actions/{actionId}", async (
            string id, string actionId, ToggleActionRequest request, HttpContext http,
            AriaDbContext db, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var note = await db.Notes.Include(n => n.AttachedActions)
                .FirstOrDefaultAsync(n => n.Id == id && n.TenantId == me.TenantId, ct);

            if (note is null) return Results.NotFound();
            if (note.Status is NoteStatus.Signed) return Results.Conflict(new { error = "Note is signed." });

            var action = note.AttachedActions.FirstOrDefault(a => a.Id == actionId);
            if (action is null) return Results.NotFound();

            // A safety-blocked action cannot be re-enabled from the UI. This is the one place a
            // clinician is not given the final say, and it is deliberate.
            if (action.BlockedReason is not null && request.Enabled)
                return Results.Conflict(new { error = action.BlockedReason });

            action.Enabled = request.Enabled;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { action.Id, action.Enabled });
        });

        // ── THE WRITE BARRIER. ──
        group.MapPost("/{id}/sign", async (
            string id, HttpContext http, SignatureService signatures, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            // Refuse on role before the note is looked up, so a refusal reads as 403 rather
            // than as a conflict — and so "you may not sign" and "no such note" are not
            // distinguishable to someone who should not be here. The service checks this
            // again; that check is the rule, this one is the status code and the leak.
            if (!me.CanSign) return me.Denied("sign a clinical note");

            var result = await signatures.SignAsync(me, id, ct);

            return result.Success
                ? Results.Ok(new { result.NoteId, result.QueuedActions, result.SkippedActions })
                : Results.Conflict(new { error = result.Blocker });
        });

        group.MapPost("/{id}/addenda", async (
            string id, AddendumRequest request, HttpContext http,
            AriaDbContext db, IAuditService audit, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var note = await db.Notes.Include(n => n.Addenda)
                .FirstOrDefaultAsync(n => n.Id == id && n.TenantId == me.TenantId, ct);

            if (note is null) return Results.NotFound();

            try
            {
                var addendum = new NoteAddendum
                {
                    Id = Guid.NewGuid().ToString("n")[..12],
                    NoteId = note.Id, AuthorId = me.DoctorId, Body = request.Body,
                };

                note.AddAddendum(addendum);
                await db.SaveChangesAsync(ct);

                await audit.WriteAsync(me.TenantId, me.DoctorId, ActorKind.Clinician,
                    AuditActions.AddendumAdded, "note", note.Id, note.PatientId, ct: ct);

                return Results.Ok(new { addendum.Id, addendum.Body, addendum.CreatedAt });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        group.MapDelete("/{id}", async (string id, HttpContext http, AriaDbContext db, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var note = await db.Notes.FirstOrDefaultAsync(n => n.Id == id && n.TenantId == me.TenantId, ct);
            if (note is null) return Results.NotFound();

            try { note.Discard(); await db.SaveChangesAsync(ct); return Results.Ok(new { discarded = id }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        // ── "Report bad suggestion": one tap, straight into the eval funnel (§9.1). ──
        app.MapPost("/v1/feedback", async (
            FeedbackRequest request, HttpContext http, AriaDbContext db,
            IAuditService audit, IAriaEventSink events, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var feedback = new Domain.Governance.Feedback
            {
                Id = Guid.NewGuid().ToString("n")[..12],
                TenantId = me.TenantId, Surface = request.Surface, TargetId = request.TargetId,
                DoctorId = me.DoctorId, Reason = request.Reason, Detail = request.Detail,
            };

            db.Feedback.Add(feedback);
            await db.SaveChangesAsync(ct);

            await audit.WriteAsync(me.TenantId, me.DoctorId, ActorKind.Clinician,
                AuditActions.FeedbackReported, request.Surface, request.TargetId,
                detail: new { request.Reason }, ct: ct);

            events.Emit(AriaEvents.BadSuggestionReported, new Dictionary<string, object?>
            {
                ["surface"] = request.Surface, ["reason"] = request.Reason,
            });

            return Results.Ok(new { feedback.Id, status = "queued for clinical review" });
        });
    }
}

public sealed record EditSpanRequest(string Text);
public sealed record ToggleActionRequest(bool Enabled);
public sealed record AddendumRequest(string Body);
public sealed record FeedbackRequest(string Surface, string? TargetId, string Reason, string? Detail);
