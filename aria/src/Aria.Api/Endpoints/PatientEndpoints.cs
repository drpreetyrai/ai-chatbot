using Aria.Agents.Agents;
using Aria.Agents.Runtime;
using Aria.Api.Auth;
using Aria.Domain;
using Aria.Infrastructure.Audit;
using Aria.Infrastructure.Persistence;
using Aria.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Aria.Api.Endpoints;

public static class PatientEndpoints
{
    public static void MapPatientEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/v1/patients");

        group.MapGet("/", async (HttpContext http, AriaDbContext db, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            // Admins configure and audit; they never see PHI (plan.md §10.1). Patients see
            // their own record, never a directory of everyone else's — enumerating the
            // clinic is not a patient capability, however scoped the individual reads are.
            if (!me.MayViewPhi) return me.Denied("view patient data");
            if (me.IsPatient) return me.Denied("list other patients");

            var today = DateOnly.FromDateTime(DateTime.Today);
            var patients = await db.Patients.AsNoTracking().Include(p => p.Flags)
                .Where(p => p.TenantId == me.TenantId)
                .ToListAsync(ct);

            return Results.Ok(patients.Select(p => new
            {
                p.Id, p.Name, p.Mrn, p.Sex, Age = p.AgeYears(today), Phone = p.MaskedPhone,
                Flags = p.Flags.Select(f => new { f.Label, Kind = f.Kind.ToString(), Severity = f.Severity.ToString() }),
            }));
        });

        group.MapGet("/{id}", async (string id, HttpContext http, AriaDbContext db, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            if (me.GuardPatientAccess(id) is { } denied) return denied;

            var patient = await db.Patients.AsNoTracking().Include(p => p.Flags)
                .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == me.TenantId, ct);

            if (patient is null) return Results.NotFound();

            return Results.Ok(new
            {
                patient.Id, patient.Name, patient.Mrn, patient.Sex,
                Age = patient.AgeYears(DateOnly.FromDateTime(DateTime.Today)),
                Phone = patient.MaskedPhone,
                patient.PreferredLanguage,
                Flags = patient.Flags.Select(f => new
                {
                    f.Label, Kind = f.Kind.ToString(), Severity = f.Severity.ToString(), f.SourceRef, f.RecordedAt,
                }),
            });
        });

        group.MapGet("/{id}/timeline", async (string id, HttpContext http, AriaDbContext db, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            if (me.GuardPatientAccess(id) is { } denied) return denied;

            var notes = await db.Notes.AsNoTracking().Include(n => n.Sections)
                .Where(n => n.PatientId == id && n.TenantId == me.TenantId && n.Status != Domain.NoteStatus.Discarded)
                .OrderByDescending(n => n.DraftCreatedAt)
                .ToListAsync(ct);

            var appointments = await db.Appointments.AsNoTracking()
                .Where(a => a.PatientId == id)
                .OrderByDescending(a => a.StartAt)
                .ToListAsync(ct);

            return Results.Ok(new
            {
                Notes = notes.Select(n => new
                {
                    n.Id, Status = n.Status.ToString(), n.DraftCreatedAt, n.SignedAt,
                    Summary = n.Sections.FirstOrDefault(s => s.Kind == Domain.NoteSectionKind.Assessment)?.Text
                           ?? n.Sections.FirstOrDefault()?.Text ?? "(no content)",
                }),
                Appointments = appointments.Select(a => new { a.Id, a.StartAt, a.Reason, a.Status }),
            });
        });

        // ── "Ask this chart" (wireframe S-05). ──
        // The scope statement travels with the answer, not buried in settings — the user needs to
        // know what it is drawn from at the moment they read it.
        group.MapPost("/{id}/ask", async (
            string id, AskRequest request, HttpContext http, ChartQaService qa,
            IAuditService audit, IAriaEventSink events, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            if (me.GuardPatientAccess(id) is { } denied) return denied;

            var context = new AgentContext(me, http.FacilityId(), id);
            var answer = await qa.AskAsync(context, id, request.Question, ct);

            await audit.WriteAsync(me.TenantId, me.DoctorId, ActorKind.Clinician,
                "CHART_QA", "patient", id, id,
                detail: new { request.Question, claims = answer.Claims.Count, answer.Interventions }, ct: ct);

            return Results.Ok(new
            {
                answer.InsufficientEvidence,
                answer.ScopeStatement,
                Claims = answer.Claims.Select(c => new
                {
                    c.Text,
                    Sources = c.Sources.Select(s => new { s.Id, s.Title, s.Citation }),
                }),
                // Surfaced, never swallowed: the user is told what the guardrails removed.
                answer.Interventions,
            });
        });

        // ── Clinical evidence drawer (wireframe S-08). ──
        app.MapPost("/v1/clinical-support", async (
            EvidenceRequest request, HttpContext http, ClinicalEvidenceService evidence, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            if (request.PatientId is { } pid && me.GuardPatientAccess(pid) is { } denied) return denied;
            if (!me.MayViewPhi) return me.Denied("view patient data");

            var context = new AgentContext(me, http.FacilityId(), request.PatientId, request.EncounterId);
            var result = await evidence.ConsiderAsync(context, request.Findings, ct);

            return Results.Ok(new
            {
                result.Findings,
                Considerations = result.Considerations.Select(c => new
                {
                    c.Title, c.Strength, c.Suggested, c.CitationId, c.Citation, c.Url,
                }),
                result.SafetyChecks,
                result.Disclaimer,
                result.NothingCited,
                result.Interventions,
                EmptyMessage = result.NothingCited
                    ? "No cited evidence found — showing nothing rather than guessing."
                    : null,
            });
        });

        // Revealing a masked identifier is an action, and actions are audited (wireframe §9.9).
        group.MapPost("/{id}/unmask", async (
            string id, HttpContext http, AriaDbContext db, IAuditService audit, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            if (me.GuardPatientAccess(id) is { } denied) return denied;

            var patient = await db.Patients.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == me.TenantId, ct);

            if (patient is null) return Results.NotFound();

            await audit.WriteAsync(me.TenantId, me.DoctorId, ActorKind.Clinician,
                AuditActions.PhiUnmasked, "patient", id, id, detail: new { field = "phone" }, ct: ct);

            return Results.Ok(new { patient.Phone, patient.Mrn });
        });
    }
}

public sealed record AskRequest(string Question);
public sealed record EvidenceRequest(string? PatientId, string? EncounterId, List<string> Findings);
