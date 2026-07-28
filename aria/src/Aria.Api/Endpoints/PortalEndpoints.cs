using System.Net.Http.Json;
using System.Text.Json;
using Aria.Agents.Agents;
using Aria.Agents.Runtime;
using Aria.Api.Auth;
using Aria.Domain;
using Aria.Domain.Messaging;
using Aria.Domain.Notes;
using Aria.Infrastructure.Persistence;
using Aria.Shared.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Aria.Api.Endpoints;

/// <summary>
/// The patient's own surface.
///
/// Every route here is implicitly scoped to the caller: there is no patient id in any
/// path, because a patient should not be able to name a patient at all. The id comes
/// from their approved account link and nowhere else, which removes a whole class of
/// "change the number in the URL" bug by construction.
/// </summary>
public static class PortalEndpoints
{
    public static void MapPortalEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/v1/portal");

        group.MapGet("/me", async (HttpContext http, AriaDbContext db, CancellationToken ct) =>
        {
            if (!TryPatient(http, out var me, out var patientId, out var denied)) return denied!;

            var patient = await db.Patients.AsNoTracking().Include(p => p.Flags)
                .FirstOrDefaultAsync(p => p.Id == patientId, ct);

            if (patient is null) return Results.NotFound(new { error = "Your record could not be found." });

            return Results.Ok(new
            {
                patient.Id, patient.Name, patient.Mrn, patient.Sex,
                Age = patient.AgeYears(DateOnly.FromDateTime(DateTime.Today)),
                // A patient sees their own phone number in full. Masking exists to stop a
                // clinician casually reading someone else's, which does not apply here.
                patient.Phone,
                patient.PreferredLanguage,
                Allergies = patient.Flags.Where(f => f.Kind == FlagKind.Allergy)
                    .Select(f => new { f.Label, Severity = f.Severity.ToString() }),
                Conditions = patient.Flags.Where(f => f.Kind == FlagKind.Condition).Select(f => f.Label),
            });
        });

        group.MapGet("/appointments", async (HttpContext http, AriaDbContext db, CancellationToken ct) =>
        {
            if (!TryPatient(http, out var me, out var patientId, out var denied)) return denied!;

            var appointments = await db.Appointments.AsNoTracking()
                .Where(a => a.PatientId == patientId && a.Status != "cancelled")
                .OrderBy(a => a.StartAt)
                .ToListAsync(ct);

            var doctors = await db.Clinicians.AsNoTracking()
                .ToDictionaryAsync(c => c.DoctorId, c => c.Name, ct);

            var now = DateTimeOffset.UtcNow;

            return Results.Ok(appointments.Select(a => new
            {
                a.Id, a.StartAt, a.DurationMinutes, a.Reason, a.Status,
                Doctor = doctors.GetValueOrDefault(a.DoctorId, "Your clinician"),
                IsPast = a.StartAt < now,
            }));
        });

        // Visit summaries — SIGNED notes only.
        //
        // A draft is a clinician's working document; showing one to the patient it is
        // about would break the product's central promise that nothing is real until a
        // human signs it.
        group.MapGet("/visits", async (HttpContext http, AriaDbContext db, CancellationToken ct) =>
        {
            if (!TryPatient(http, out var me, out var patientId, out var denied)) return denied!;

            var notes = await db.Notes.AsNoTracking().Include(n => n.Sections)
                .Where(n => n.PatientId == patientId && n.Status == NoteStatus.Signed)
                .OrderByDescending(n => n.SignedAt)
                .ToListAsync(ct);

            var doctors = await db.Clinicians.AsNoTracking()
                .ToDictionaryAsync(c => c.DoctorId, c => c.Name, ct);

            return Results.Ok(notes.Select(n => new
            {
                n.Id,
                n.SignedAt,
                Clinician = doctors.GetValueOrDefault(n.SignedBy ?? n.DoctorId, "Your clinician"),
                // Assessment and Plan only. Subjective and Objective are clinical shorthand
                // written for another clinician, and reading them cold causes alarm without
                // adding understanding.
                Summary = n.Sections.FirstOrDefault(s => s.Kind == NoteSectionKind.Assessment)?.Text,
                Plan = n.Sections.FirstOrDefault(s => s.Kind == NoteSectionKind.Plan)?.Text,
            }));
        });

        group.MapGet("/messages", async (HttpContext http, AriaDbContext db, CancellationToken ct) =>
        {
            if (!TryPatient(http, out var me, out var patientId, out var denied)) return denied!;

            var threads = await db.Threads.AsNoTracking()
                .Where(t => t.PatientId == patientId).Select(t => t.Id).ToListAsync(ct);

            var messages = await db.Messages.AsNoTracking()
                .Where(m => threads.Contains(m.ThreadId))
                // Only what actually reached them. A draft awaiting approval, or one still
                // inside its undo window, has not been sent and must not appear.
                .Where(m => m.Direction == MessageDirection.Inbound
                         || m.Status == MessageStatus.Sent
                         || m.Status == MessageStatus.Delivered)
                .OrderBy(m => m.CreatedAt)
                .ToListAsync(ct);

            return Results.Ok(messages.Select(m => new
            {
                m.Id, Direction = m.Direction.ToString(), m.Body, m.CreatedAt,
                FromClinic = m.Direction == MessageDirection.Outbound,
            }));
        });
    }

    /// <summary>
    /// Resolves the caller's own patient id, or explains why they have none.
    ///
    /// An approved patient account is always linked, so a missing link means the account
    /// was approved incorrectly — worth saying out loud rather than returning an empty page.
    /// </summary>
    internal static bool TryPatient(
        HttpContext http, out ClinicianIdentity identity, out string patientId, out IResult? denied)
    {
        identity = default!;
        patientId = string.Empty;
        denied = null;

        if (!http.TryIdentity(out identity)) { denied = Results.Unauthorized(); return false; }

        if (!identity.IsPatient) { denied = identity.Denied("use the patient portal"); return false; }

        if (string.IsNullOrWhiteSpace(identity.PatientId))
        {
            denied = Results.Json(new
            {
                error = "Your account is not linked to a patient record. Please contact the practice.",
            }, statusCode: StatusCodes.Status409Conflict);
            return false;
        }

        patientId = identity.PatientId;
        return true;
    }
}

/// <summary>
/// The conversational assistant, for both audiences.
///
/// One endpoint, because it is one assistant — what differs is the grounding and the
/// rules, and those are decided from the caller's role rather than from anything the
/// client sends.
/// </summary>
public static class AssistantEndpoints
{
    public static void MapAssistantEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/v1/assistant");

        group.MapPost("/chat", async (
            AssistantChatRequest request, HttpContext http,
            AssistantService assistant, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            // Which record the conversation is grounded in is decided HERE, from the
            // caller's identity — never from the request body. A patient can only ever
            // talk about themselves; a clinician names the patient they are looking at.
            string? patientId;

            if (me.IsPatient)
            {
                patientId = me.PatientId;
                if (string.IsNullOrWhiteSpace(patientId))
                    return Results.Conflict(new { error = "Your account is not linked to a patient record." });
            }
            else
            {
                patientId = request.PatientId;
                if (patientId is not null && me.GuardPatientAccess(patientId) is { } denied) return denied;
            }

            if (string.IsNullOrWhiteSpace(request.Message))
                return Results.BadRequest(new { error = "Say something." });

            // A conversation belongs to one person and one record, so its id is derived
            // rather than accepted — a client cannot join someone else's thread.
            var conversationId = $"{me.TenantId}:{me.DoctorId}:{patientId ?? "general"}";

            var context = new AgentContext(me, http.FacilityId(), patientId);
            var reply = await assistant.AskAsync(context, conversationId, request.Message.Trim(), ct);

            return Results.Ok(new
            {
                reply.Text,
                reply.Escalated,
                reply.Degraded,
                reply.Interventions,
                Sources = reply.Sources.Select(s => new { s.Id, s.Title, s.Citation }),
            });
        });

        group.MapGet("/history", async (
            string? patientId, HttpContext http, AssistantService assistant, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var scoped = me.IsPatient ? me.PatientId : patientId;
            if (scoped is not null && !me.IsPatient && me.GuardPatientAccess(scoped) is { } denied) return denied;

            var conversationId = $"{me.TenantId}:{me.DoctorId}:{scoped ?? "general"}";
            var turns = await assistant.TranscriptAsync(conversationId, ct);

            return Results.Ok(turns.Select(t => new { t.Role, t.Text, t.At }));
        });
    }
}

/// <summary>
/// Speech, done the way Azure intends: the browser streams audio straight to the
/// Speech service using a short-lived token this endpoint mints.
///
/// The alternative — proxying microphone audio through our API — would put PHI-bearing
/// audio through an extra hop for no benefit, and add latency to the one thing that has
/// to feel instant.
/// </summary>
public static class SpeechEndpoints
{
    public static void MapSpeechEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/speech/token", async (
            HttpContext http, AriaOptions options, IHttpClientFactory factory, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            if (!me.IsClinician) return me.Denied("start ambient capture");

            if (!options.Speech.IsConfigured)
            {
                // Said plainly, so the encounter screen can fall back to the scripted
                // consultation and tell the clinician that is what it is doing.
                return Results.Ok(new
                {
                    configured = false,
                    reason = "Azure AI Speech is not configured. Set SPEECH_KEY and SPEECH_REGION in .env.",
                });
            }

            var region = options.Speech.ResolvedRegion;
            var client = factory.CreateClient();

            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"https://{region}.api.cognitive.microsoft.com/sts/v1.0/issueToken");
            request.Headers.Add("Ocp-Apim-Subscription-Key", options.Speech.ApiKey);

            using var response = await client.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                return Results.Json(new
                {
                    configured = false,
                    reason = $"Speech token request failed ({(int)response.StatusCode}). " +
                             "Check SPEECH_KEY and that SPEECH_REGION matches the resource.",
                }, statusCode: StatusCodes.Status502BadGateway);
            }

            return Results.Ok(new
            {
                configured = true,
                token = await response.Content.ReadAsStringAsync(ct),
                region,
                // The clinical vocabulary the recogniser would otherwise mangle. Drug names
                // are the ones that matter: "azithromycin" misheard is a safety problem.
                phrases = new[]
                {
                    "amoxicillin", "azithromycin", "paracetamol", "salbutamol", "co-amoxiclav",
                    "penicillin", "clarithromycin", "doxycycline", "ibuprofen", "amlodipine",
                    "SpO2", "CRP", "CBC", "auscultation", "crackles", "dyspnoea",
                    "community-acquired pneumonia", "exertional breathlessness",
                },
            });
        });
    }
}

public sealed record AssistantChatRequest(string Message, string? PatientId);
