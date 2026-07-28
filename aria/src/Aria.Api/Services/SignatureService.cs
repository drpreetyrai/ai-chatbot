using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aria.Domain;
using Aria.Domain.Encounters;
using Aria.Domain.Governance;
using Aria.Domain.Notes;
using Aria.Infrastructure.Audit;
using Aria.Infrastructure.Persistence;
using Aria.Shared.Configuration;
using Aria.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Aria.Api.Services;

public sealed record SignResult(
    bool Success,
    string? NoteId,
    IReadOnlyList<string> QueuedActions,
    IReadOnlyList<string> SkippedActions,
    string? Blocker);

/// <summary>
/// THE WRITE BARRIER (plan.md §1.2, Invariant 1).
///
/// This is the only place in the entire system that may enqueue an external write. Everything the
/// product does to the outside world — the EHR document, the prescription, the lab order, the
/// calendar booking, the WhatsApp summary — is emitted from here, inside the same database
/// transaction as the signature itself.
///
/// Three things enforce that, independently:
///   1. This method is the only writer to <see cref="AriaDbContext.Outbox"/>.
///   2. The outbox table carries a CHECK constraint requiring a non-empty NoteId.
///   3. Aria.Api cannot reference Aria.Integrations, so it could not call a vendor if it wanted to.
///
/// One barrier is testable. Five scattered writes are not.
/// </summary>
public sealed class SignatureService(
    AriaDbContext db,
    IAuditService audit,
    IAriaEventSink events,
    AriaOptions options,
    ILogger<SignatureService> logger)
{
    public async Task<SignResult> SignAsync(
        ClinicianIdentity identity, string noteId, CancellationToken ct = default)
    {
        // Only a clinician signs. This is a legal act, not a UI affordance.
        if (!identity.CanSign)
        {
            logger.LogWarning("{Role} {DoctorId} attempted to sign note {NoteId}.", identity.Role, identity.DoctorId, noteId);
            await audit.WriteAsync(identity.TenantId, identity.DoctorId, ActorKind.System,
                AuditActions.GuardrailBlocked, "note", noteId, outcome: "refused",
                detail: new { reason = "role_cannot_sign", role = identity.Role.ToString() }, ct: ct);

            return new SignResult(false, noteId, [], [], $"Role '{identity.Role}' cannot sign clinical notes.");
        }

        var note = await db.Notes
            .Include(n => n.Sections)
            .Include(n => n.AttachedActions)
            .FirstOrDefaultAsync(n => n.Id == noteId && n.TenantId == identity.TenantId, ct);

        if (note is null) return new SignResult(false, noteId, [], [], "Note not found.");

        // Idempotent by design: a double-tap on a flaky connection must not sign twice.
        if (note.Status is NoteStatus.Signed)
        {
            logger.LogInformation("Note {NoteId} is already signed; returning the original outcome.", noteId);
            var existing = await db.Outbox.Where(o => o.NoteId == noteId)
                .Select(o => o.ActionType.ToString()).ToListAsync(ct);
            return new SignResult(true, noteId, existing, [], null);
        }

        if (!note.IsSignable(out var blocker))
            return new SignResult(false, noteId, [], [], blocker);

        var patient = await db.Patients.FirstAsync(p => p.Id == note.PatientId, ct);

        // ── Everything below happens in ONE transaction: the signature, the audit row, and the
        //    outbox entries. Either the note is signed and the work is queued, or neither. ──
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var signatureHash = ComputeSignature(note, identity);
        note.Sign(identity.DoctorId, signatureHash);
        note.EditDistance = ComputeEditDistance(note);

        var queued = new List<string>();
        var skipped = new List<string>();

        foreach (var action in note.AttachedActions)
        {
            // A clinician unticking a box is a decision we record, not an error.
            if (!action.Enabled)
            {
                skipped.Add($"{action.Kind}: unticked by clinician");
                continue;
            }

            // A deterministic safety check blocked this. It cannot be overridden from the UI,
            // and it is not silently dropped either — the clinician sees why.
            if (action.BlockedReason is not null)
            {
                skipped.Add($"{action.Kind}: {action.BlockedReason}");
                logger.LogWarning("Action {Kind} on note {NoteId} blocked: {Reason}",
                    action.Kind, note.Id, action.BlockedReason);
                continue;
            }

            var item = new OutboxItem
            {
                Id = Guid.NewGuid().ToString("n"),
                TenantId = note.TenantId,
                NoteId = note.Id,                                  // ← the constraint the DB enforces
                ActionType = action.Kind,
                PayloadJson = Enrich(action, note, patient, identity),
                IdempotencyKey = $"{note.Id}:{action.Kind}:1",
                // Outbound patient messages are held for the undo window. Reversibility as a
                // schedule, not a recall (plan.md §3.5).
                VisibleAfter = action.Kind is OutboxActionType.PatientMessage
                    ? DateTimeOffset.UtcNow.AddSeconds(options.Safety.MessageUndoSeconds)
                    : null,
            };

            db.Outbox.Add(item);
            queued.Add(action.Kind.ToString());
        }

        var encounter = await db.Encounters.FirstOrDefaultAsync(e => e.Id == note.EncounterId, ct);
        if (encounter is not null && EncounterStateMachine.CanTransition(encounter.State, EncounterState.Signed))
            encounter.State = EncounterState.Signed;

        await db.SaveChangesAsync(ct);

        // The audit row is written before the transaction commits, so an auditable record of the
        // signature exists for every outbox entry that exists — never one without the other.
        await audit.WriteAsync(
            identity.TenantId, identity.DoctorId, ActorKind.Clinician, AuditActions.NoteSigned,
            "note", note.Id, note.PatientId, note.ModelVersion, note.PromptVersion,
            humanEdits: note.AllSpans.Count(s => s.EditedByHuman),
            detail: new
            {
                queued,
                skipped,
                signatureHash,
                editDistance = note.EditDistance,
                lowConfidenceAccepted = note.AllSpans.Count(s => s.Band is ConfidenceBand.Low && s.AcceptedByHuman),
            }, ct: ct);

        await tx.CommitAsync(ct);

        events.Emit(AriaEvents.NoteSigned, new Dictionary<string, object?>
        {
            ["note_id"] = note.Id,
            ["patient_id"] = note.PatientId,
            ["doctor_id"] = identity.DoctorId,
            ["edit_distance"] = note.EditDistance,
            ["queued_actions"] = queued.Count,
            ["skipped_actions"] = skipped.Count,
            ["time_to_sign_s"] = Math.Round((DateTimeOffset.UtcNow - note.DraftCreatedAt).TotalSeconds, 1),
        });

        logger.LogInformation("Note {NoteId} signed by {DoctorId}. {Queued} action(s) queued, {Skipped} skipped.",
            note.Id, identity.DoctorId, queued.Count, skipped.Count);

        return new SignResult(true, note.Id, queued, skipped, null);
    }

    /// <summary>
    /// Binds the signature to the exact content signed. If a byte of the note changed afterwards,
    /// this hash would no longer reproduce — which is what makes the audit row meaningful.
    /// </summary>
    private static string ComputeSignature(Note note, ClinicianIdentity identity)
    {
        var canonical = string.Join('|',
            note.Id, note.PatientId, identity.DoctorId,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            string.Join('\n', note.Sections.OrderBy(s => s.Kind).Select(s => $"{s.Kind}:{s.Text}")));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// The share of AI-authored spans the clinician changed. Shown to the clinician, not just to
    /// analytics — a quiet contract that we are measuring how wrong we were (wireframe S-04).
    /// </summary>
    private static double ComputeEditDistance(Note note)
    {
        var spans = note.AllSpans.ToList();
        if (spans.Count == 0) return 0;
        return Math.Round((double)spans.Count(s => s.EditedByHuman) / spans.Count * 100, 1);
    }

    /// <summary>
    /// Adds what the worker needs to perform the action, so the dispatcher never has to re-read
    /// clinical context — and never has to make a clinical decision of its own.
    /// </summary>
    private static string Enrich(
        AttachedAction action, Note note, Domain.Patients.Patient patient, ClinicianIdentity identity)
    {
        using var doc = JsonDocument.Parse(action.PayloadJson);
        var payload = doc.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => (object?)p.Value.ToString());

        payload["noteId"] = note.Id;
        payload["tenantId"] = note.TenantId;
        payload["patientMrn"] = patient.Mrn;
        payload["patientPhone"] = patient.Phone;
        payload["patientName"] = patient.Name;
        payload["patientLanguage"] = patient.PreferredLanguage;
        payload["doctorId"] = identity.DoctorId;
        payload["doctorName"] = identity.Name;
        payload["calendarId"] = identity.GoogleCalendarId;

        if (action.Kind is OutboxActionType.EhrDocumentWrite)
            payload["content"] = string.Join("\n\n", note.Sections
                .OrderBy(s => s.Kind)
                .Select(s => $"{s.Kind.ToString().ToUpperInvariant()}\n{s.Text}"));

        return JsonSerializer.Serialize(payload);
    }
}
