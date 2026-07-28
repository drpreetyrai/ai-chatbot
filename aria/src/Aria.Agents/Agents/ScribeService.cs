using Aria.Agents.Memory;
using Aria.Agents.Models;
using Aria.Agents.Runtime;
using Aria.Agents.Tools;
using Aria.Domain;
using Aria.Domain.Contracts;
using Aria.Domain.Notes;
using Aria.Infrastructure.Persistence;
using Aria.Safety;
using Aria.Shared.Telemetry;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aria.Agents.Agents;

/// <summary>
/// Turns a finished encounter into a signable draft note.
///
/// The order of operations matters and is not arbitrary:
///   1. The model drafts.
///   2. Provenance enforcement DELETES any sentence that cannot be traced to the transcript.
///   3. The deterministic allergy checker runs over the FINAL plan and OVERRIDES the model.
///   4. Attached actions are assembled but nothing fires — they are checkboxes until signature.
///
/// Step 3 is deliberately last. Whatever the model concluded, a contraindicated drug does not
/// reach the clinician as a suggestion they might miss; it reaches them as a blocked item with
/// the conflict spelled out.
/// </summary>
public sealed class ScribeService(
    GuardedAgentRunner runner,
    AriaDbContext db,
    ClinicalToolFactory toolFactory,
    AllergyConflictChecker allergyChecker,
    IClinicianPreferenceStore preferences,
    IAriaEventSink events,
    ILogger<ScribeService> logger)
{
    public async Task<Note> DraftAsync(AgentContext context, string encounterId, CancellationToken ct = default)
    {
        var encounter = await db.Encounters.FirstAsync(e => e.Id == encounterId, ct);
        var patient = await db.Patients.Include(p => p.Flags)
            .FirstAsync(p => p.Id == encounter.PatientId, ct);

        var segments = await db.TranscriptSegments.AsNoTracking()
            .Where(s => s.EncounterId == encounterId && s.IsFinal)
            .OrderBy(s => s.StartMs)
            .ToListAsync(ct);

        var transcript = string.Join("\n", segments.Select(s => $"[{s.StartMs}] {s.Speaker} {s.Text}"));
        var transcriptEndMs = segments.Count == 0 ? 0 : segments[^1].EndMs;

        var note = new Note
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            EncounterId = encounterId,
            TenantId = context.TenantId,
            PatientId = patient.Id,
            DoctorId = context.DoctorId,
            TemplateId = $"{context.Department.ToLowerInvariant()}.consultation",
        };

        var scopedContext = context with { PatientId = patient.Id, EncounterId = encounterId };

        var result = await runner.RunAsync<DraftNoteResult>(
            agentId: AgentIds.Scribe,
            context: scopedContext,
            promptId: AgentIds.Scribe,
            userMessage: $"Draft the note for this encounter.\n\nTRANSCRIPT:\n{transcript}",
            task: ModelTask.NoteSynthesis,
            tools: toolFactory.ForScribe(scopedContext),
            contextProviders: [
                new PatientContextProvider(db, scopedContext),
                new ClinicianPreferenceProvider(preferences, scopedContext),
            ],
            enforceOutput: (draft, scope) => runner.Guards.EnforceProvenance(draft, scope, transcriptEndMs),
            ct: ct);

        note.PromptVersion = runner.Prompts.Resolve(AgentIds.Scribe).Reference;
        note.ModelVersion = "aria-scribe-2.4";

        // ── Degraded path. The clinic finishes the day with every model down (wireframe §10). ──
        if (!result.Allowed || result.Value is null)
        {
            logger.LogWarning("Scribe unavailable for {EncounterId} ({Reason}). Offering transcript-only.",
                encounterId, result.DenialReason);

            note.DraftUnavailable = true;
            note.Sections.Add(new NoteSection { Id = NewId(), Kind = NoteSectionKind.Subjective });

            events.Emit(AriaEvents.NoteDraftCompleted, new Dictionary<string, object?>
            {
                ["encounter_id"] = encounterId, ["degraded"] = true, ["reason"] = result.DenialReason,
            });

            db.Notes.Add(note);
            await db.SaveChangesAsync(ct);
            return note;
        }

        var draft = result.Value;

        // ── Map the model's spans onto the domain. ──
        //
        // Confidence is capped by what the RECOGNISER heard, not only by what the model
        // claims — see AddSection. A hosted model self-reports 0.9+ on nearly everything,
        // so with a live model the review gate quietly stopped engaging and notes became
        // signable on the model's own say-so.
        AddSection(note, NoteSectionKind.Subjective, draft.Subjective, segments);
        AddSection(note, NoteSectionKind.Objective, draft.Objective, segments);
        AddSection(note, NoteSectionKind.Assessment, draft.Assessment, segments);
        AddSection(note, NoteSectionKind.Plan, draft.Plan, segments);

        foreach (var c in draft.Codes)
            note.Codes.Add(new CodeSuggestion
            {
                Code = c.Code, System = c.System, Display = c.Display, Confidence = c.Confidence,
            });

        // Actions are assembled first so the safety pass below has something to block. Getting
        // this order wrong makes the prescription block a silent no-op — the span gets marked but
        // the pharmacy order still fires, which is the exact failure the check exists to prevent.
        BuildAttachedActions(note, patient, context);

        // ── The deterministic override. Runs last over the final plan, wins outright. ──
        ApplyAllergySafety(note, patient.Flags, context, encounterId);

        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);

        events.Emit(AriaEvents.NoteDraftCompleted, new Dictionary<string, object?>
        {
            ["encounter_id"] = encounterId,
            ["note_id"] = note.Id,
            ["spans"] = note.AllSpans.Count(),
            ["low_confidence"] = note.LowConfidenceSpanCount,
            ["interventions"] = result.Interventions.Count,
            ["degraded"] = false,
        });

        logger.LogInformation("Drafted note {NoteId}: {Spans} spans, {Low} low-confidence, {Interventions} guardrail interventions.",
            note.Id, note.AllSpans.Count(), note.LowConfidenceSpanCount, result.Interventions.Count);

        return note;
    }

    private static void AddSection(
        Note note, NoteSectionKind kind, List<DraftSpan> spans,
        IReadOnlyList<Domain.Encounters.TranscriptSegment> segments)
    {
        if (spans.Count == 0) return;

        var section = new NoteSection { Id = NewId(), Kind = kind };
        var ordinal = 0;
        foreach (var s in spans)
            section.Spans.Add(new NoteSpan
            {
                Id = NewId(),
                Ordinal = ordinal++,     // owned collections come back unordered without this
                Text = s.Text,
                TranscriptStartMs = s.StartMs,
                TranscriptEndMs = s.EndMs,
                Confidence = Math.Min(s.Confidence, HeardConfidence(segments, s.StartMs, s.EndMs)),
                FlagReason = string.IsNullOrWhiteSpace(s.FlagReason) ? null : s.FlagReason,
            });

        note.Sections.Add(section);
    }

    /// <summary>
    /// The recogniser's own confidence over the audio a span cites.
    ///
    /// The weakest word in the passage decides, not the average: a sentence containing one
    /// badly-heard drug name is exactly the sentence a clinician must look at, and an
    /// average would dilute it into invisibility.
    ///
    /// Returns 1 when nothing overlaps — a span with no matching audio is a provenance
    /// problem, and provenance enforcement has already deleted those. Guessing "low" here
    /// would flag every span in a note the enforcement pass had already cleared.
    /// </summary>
    private static double HeardConfidence(
        IReadOnlyList<Domain.Encounters.TranscriptSegment> segments, long startMs, long endMs)
    {
        var overlapping = segments
            .Where(seg => seg.StartMs < endMs && seg.EndMs > startMs)
            .Select(seg => seg.Confidence)
            .ToList();

        return overlapping.Count == 0 ? 1.0 : overlapping.Min();
    }

    /// <summary>
    /// Scans the finished plan for contraindications and rewrites the note in place.
    ///
    /// Note that this does not "warn". It marks the span, and it blocks the prescription action so
    /// it cannot be enabled from the UI. A warning a tired clinician can scroll past is not a
    /// safety control.
    /// </summary>
    private void ApplyAllergySafety(Note note, List<Domain.Patients.PatientFlag> flags, AgentContext ctx, string encounterId)
    {
        var planSpans = note.Sections
            .Where(s => s.Kind is NoteSectionKind.Plan)
            .SelectMany(s => s.Spans)
            .ToList();

        var conflicts = allergyChecker.Check(flags, planSpans.Select(s => s.Text));
        if (conflicts.Count == 0) return;

        foreach (var conflict in conflicts)
        {
            logger.LogError(
                "ALLERGY CONFLICT in note {NoteId}: '{Drug}' vs recorded '{Allergy}'. Blocking the prescription action.",
                note.Id, conflict.DrugLabel, conflict.AllergyLabel);

            foreach (var span in planSpans.Where(s => s.Text.Contains(conflict.DrugCode, StringComparison.OrdinalIgnoreCase)))
                span.Text = $"⚠ CONTRAINDICATED — {conflict.AllergyLabel}: {span.Text}";

            events.Emit(AriaEvents.GuardrailPrefix + GuardrailReason.AllergyConflict, new Dictionary<string, object?>
            {
                ["note_id"] = note.Id,
                ["encounter_id"] = encounterId,
                ["drug"] = conflict.DrugCode,
                ["allergy"] = conflict.AllergyCode,
                ["severity"] = conflict.Severity.ToString(),
            });
        }

        note.AttachedActions
            .Where(a => a.Kind is OutboxActionType.PharmacyOrder)
            .ToList()
            .ForEach(a => a.BlockedReason =
                $"Allergy conflict: {string.Join("; ", conflicts.Select(c => $"{c.DrugLabel} vs {c.AllergyLabel}"))}");
    }

    /// <summary>
    /// The checkboxes on the Note Review screen. Assembling them here — and only here — is what
    /// makes signature the single place that commits five systems at once, and the single place
    /// that stops all five.
    /// </summary>
    private static void BuildAttachedActions(Note note, Domain.Patients.Patient patient, AgentContext ctx)
    {
        var plan = note.Sections.FirstOrDefault(s => s.Kind is NoteSectionKind.Plan);
        if (plan is null) return;

        var planText = plan.Text;

        if (planText.Contains("mg", StringComparison.OrdinalIgnoreCase))
            note.AttachedActions.Add(new AttachedAction
            {
                Id = NewId(), Kind = OutboxActionType.PharmacyOrder,
                Description = "Prescription to pharmacy",
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { noteId = note.Id, patientId = patient.Id }),
            });

        if (planText.Contains("X-ray", StringComparison.OrdinalIgnoreCase)
            || planText.Contains("CBC", StringComparison.OrdinalIgnoreCase)
            || planText.Contains("blood count", StringComparison.OrdinalIgnoreCase))
            note.AttachedActions.Add(new AttachedAction
            {
                Id = NewId(), Kind = OutboxActionType.LabOrder,
                Description = "Investigations: imaging and bloods",
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { noteId = note.Id, patientId = patient.Id }),
            });

        if (planText.Contains("review", StringComparison.OrdinalIgnoreCase)
            || planText.Contains("follow", StringComparison.OrdinalIgnoreCase))
            note.AttachedActions.Add(new AttachedAction
            {
                Id = NewId(), Kind = OutboxActionType.CalendarBooking,
                Description = "Book follow-up appointment",
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    noteId = note.Id, patientId = patient.Id, doctorId = ctx.DoctorId,
                    preferredDays = 3,
                }),
            });

        note.AttachedActions.Add(new AttachedAction
        {
            Id = NewId(), Kind = OutboxActionType.EhrDocumentWrite,
            Description = "Write note to the EHR (FHIR DocumentReference)",
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { noteId = note.Id }),
        });

        note.AttachedActions.Add(new AttachedAction
        {
            Id = NewId(), Kind = OutboxActionType.PatientMessage,
            Description = "WhatsApp visit summary and medicine reminders",
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                noteId = note.Id, patientId = patient.Id, templateId = "post_visit_summary_v3",
            }),
        });
    }

    private static string NewId() => Guid.NewGuid().ToString("n")[..12];
}
