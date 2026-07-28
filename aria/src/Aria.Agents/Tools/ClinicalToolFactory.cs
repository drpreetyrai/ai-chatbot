using System.ComponentModel;
using System.Text.Json;
using Aria.Agents.Middleware;
using Aria.Agents.Runtime;
using Aria.Domain;
using Aria.Domain.Contracts;
using Aria.Infrastructure.Persistence;
using Aria.Infrastructure.Retrieval;
using Aria.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Aria.Agents.Tools;

/// <summary>
/// Builds the tool set for one agent, in one context.
///
/// Read the signatures below carefully: not one of them accepts a tenant, patient, doctor or
/// encounter id. Those are captured from <see cref="AgentContext"/> in the closure. The model
/// can ask for a different query; it cannot ask for a different patient. Widening scope is not
/// discouraged here — it is unreachable (plan.md §4.1, rule 2).
/// </summary>
public sealed class ClinicalToolFactory(
    AriaDbContext db,
    ISearchIndex search,
    AllergyConflictChecker allergyChecker)
{
    public IList<AITool> ForScribe(AgentContext ctx) =>
    [
        GetEncounterTranscript(ctx),
        GetPatientSummary(ctx),
        LookupPatientAllergies(ctx),
        CheckAllergyConflict(ctx),
    ];

    public IList<AITool> ForChartQa(AgentContext ctx) =>
    [
        SearchPatientRecord(ctx),
        GetPatientSummary(ctx),
    ];

    public IList<AITool> ForClinicalEvidence(AgentContext ctx) =>
    [
        SearchGuidelines(ctx),
        GetGuidelineSection(ctx),
        LookupPatientAllergies(ctx),
        CheckAllergyConflict(ctx),
    ];

    public IList<AITool> ForPatientComms(AgentContext ctx) =>
    [
        GetApprovedTemplates(ctx),
        CheckServiceWindow(ctx),
        GetPatientSummary(ctx),
    ];

    // ─────────────────────────────────────────────────────────────────────────
    //  Read tools
    // ─────────────────────────────────────────────────────────────────────────

    private AITool GetEncounterTranscript(AgentContext ctx) => AIFunctionFactory.Create(
        async ([Description("Optional start offset in milliseconds")] long? fromMs,
               [Description("Optional end offset in milliseconds")] long? toMs,
               CancellationToken ct) =>
        {
            if (ctx.EncounterId is null) return "No encounter is in context.";

            var segments = await db.TranscriptSegments.AsNoTracking()
                .Where(s => s.EncounterId == ctx.EncounterId && s.IsFinal)
                .Where(s => (fromMs == null || s.StartMs >= fromMs) && (toMs == null || s.EndMs <= toMs))
                .OrderBy(s => s.StartMs)
                .ToListAsync(ct);

            return string.Join("\n", segments.Select(s => $"[{s.StartMs}] {s.Speaker} {s.Text}"));
        },
        "get_encounter_transcript",
        "Returns the transcript of the encounter currently in context, as timestamped lines.");

    private AITool GetPatientSummary(AgentContext ctx) => AIFunctionFactory.Create(
        async (CancellationToken ct) =>
        {
            if (ctx.PatientId is null) return "No patient is in context.";

            var patient = await db.Patients.AsNoTracking()
                .Include(p => p.Flags)
                .FirstOrDefaultAsync(p => p.Id == ctx.PatientId && p.TenantId == ctx.TenantId, ct);

            if (patient is null) return "Patient not found in this tenant.";

            var today = DateOnly.FromDateTime(DateTime.Today);
            var allergies  = patient.Flags.Where(f => f.Kind is FlagKind.Allergy).Select(f => f.Label).ToList();
            var conditions = patient.Flags.Where(f => f.Kind is FlagKind.Condition).Select(f => f.Label).ToList();

            return JsonSerializer.Serialize(new
            {
                age = patient.AgeYears(today),
                sex = patient.Sex,
                allergies = allergies.Count > 0 ? allergies : ["none recorded"],
                conditions,
                preferredLanguage = patient.PreferredLanguage,
                // Note what is absent: no MRN, no phone, no name. The model does not need them
                // to write a note, so it does not get them (plan.md §6.1, PHI minimisation).
            });
        },
        "get_patient_summary",
        "Allergies, conditions and demographics for the patient in context. Contains no direct identifiers.");

    private AITool LookupPatientAllergies(AgentContext ctx) => AIFunctionFactory.Create(
        async (CancellationToken ct) =>
        {
            if (ctx.PatientId is null) return "No patient is in context.";
            var flags = await db.PatientFlags.AsNoTracking()
                .Where(f => f.PatientId == ctx.PatientId && f.Kind == FlagKind.Allergy)
                .ToListAsync(ct);

            return flags.Count == 0
                ? "No allergies recorded."
                : string.Join("; ", flags.Select(f => $"{f.Label} ({f.Severity})"));
        },
        "lookup_patient_allergies",
        "Recorded allergies for the patient in context.");

    /// <summary>
    /// Deterministic and authoritative. If this disagrees with the model, this wins — which is
    /// why its description tells the model exactly that.
    /// </summary>
    private AITool CheckAllergyConflict(AgentContext ctx) => AIFunctionFactory.Create(
        async ([Description("Drug name or free text such as 'amoxicillin 500mg BD'")] string drug,
               CancellationToken ct) =>
        {
            if (ctx.PatientId is null) return "No patient is in context.";

            var flags = await db.PatientFlags.AsNoTracking()
                .Where(f => f.PatientId == ctx.PatientId)
                .ToListAsync(ct);

            var conflict = allergyChecker.CheckOne(flags, drug);

            return conflict is null
                ? $"CLEAR: no recorded allergy conflicts with '{drug}'."
                : $"CONFLICT ({conflict.Severity}): '{conflict.DrugLabel}' matches '{conflict.AllergyLabel}'. " +
                  $"{conflict.Explanation} This finding is authoritative — do not propose this drug.";
        },
        "check_allergy_conflict",
        "Deterministic contraindication check against the patient's recorded allergies. " +
        "Its verdict overrides your own judgement — always call it before proposing any medication.");

    private AITool SearchPatientRecord(AgentContext ctx) => AIFunctionFactory.Create(
        async ([Description("What to look for in this patient's record")] string query,
               [Description("How many results, maximum 8")] int topK,
               CancellationToken ct) =>
        {
            if (ctx.PatientId is null) return "No patient is in context.";

            var docs = await search.SearchPatientRecordAsync(
                query, ctx.TenantId, ctx.PatientId, Math.Clamp(topK, 1, 8), ct);

            RegisterCitations(docs);
            return Render(docs, "No matching entries in this patient's signed record.");
        },
        "search_patient_record",
        "Retrieval over THIS patient's signed records only. Results carry [SOURCE:id] markers — " +
        "cite those ids exactly and never invent one.");

    private AITool SearchGuidelines(AgentContext ctx) => AIFunctionFactory.Create(
        async ([Description("Clinical question or findings to search guidance for")] string query,
               [Description("Optional specialty filter")] string? specialty,
               [Description("How many results, maximum 6")] int topK,
               CancellationToken ct) =>
        {
            var docs = await search.SearchGuidelinesAsync(
                query, ctx.GuidelinePackVersion, specialty, Math.Clamp(topK, 1, 6), ct);

            RegisterCitations(docs);
            return Render(docs, "No guideline sections matched. Report that no cited evidence was found.");
        },
        "search_guidelines",
        "Retrieval over the approved guideline pack. Results carry [SOURCE:id] markers — " +
        "every consideration you propose must cite one of those ids exactly.");

    private AITool GetGuidelineSection(AgentContext ctx) => AIFunctionFactory.Create(
        async ([Description("Guideline id exactly as returned by search_guidelines")] string guidelineId,
               CancellationToken ct) =>
        {
            var doc = await search.GetGuidelineAsync(guidelineId, ct);
            if (doc is null) return $"No guideline section with id '{guidelineId}'. Do not cite it.";

            RegisterCitations([doc]);
            return $"[SOURCE:{doc.Id}] {doc.Title}\n{doc.Text}\nCitation: {doc.Citation}";
        },
        "get_guideline_section",
        "Fetch one guideline section by id, to quote or cite it.");

    private AITool GetApprovedTemplates(AgentContext ctx) => AIFunctionFactory.Create(
        async ([Description("Intent such as clinical_qa, appointment_reminder, reschedule_offer")] string intent,
               CancellationToken ct) =>
        {
            var templates = await db.MessageTemplates.AsNoTracking()
                .Where(t => t.TenantId == ctx.TenantId && t.Active && t.Intent == intent)
                .ToListAsync(ct);

            return templates.Count == 0
                ? $"No approved template for intent '{intent}'. You must escalate to a human instead."
                : JsonSerializer.Serialize(templates.Select(t => new { t.Id, t.Intent, t.Language, t.Parameters, t.Body }));
        },
        "get_approved_templates",
        "Approved patient-message templates. You may ONLY fill their parameters — never write free prose to a patient.");

    private AITool CheckServiceWindow(AgentContext ctx) => AIFunctionFactory.Create(
        async (CancellationToken ct) =>
        {
            if (ctx.ThreadId is null) return "No message thread is in context.";

            var thread = await db.Threads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ctx.ThreadId, ct);
            if (thread is null) return "Thread not found.";

            var remaining = thread.WindowRemaining(DateTimeOffset.UtcNow);
            return remaining is null
                ? "Service window CLOSED — only pre-approved templates may be sent."
                : $"Service window open, {remaining.Value.TotalHours:F1} hours remaining.";
        },
        "check_service_window",
        "Remaining WhatsApp 24-hour service window for the thread in context.");

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records which citation ids the model has legitimately seen this turn. The output guard
    /// checks every citation against this set, so a fabricated id cannot survive to render.
    /// </summary>
    private static void RegisterCitations(IEnumerable<RetrievedDocument> docs)
    {
        var scope = GuardrailScope.Current;
        if (scope is null) return;

        foreach (var d in docs)
        {
            scope.ResolvableCitationIds.Add(d.Id);
            if (!scope.Documents.Any(x => x.Id == d.Id)) scope.Documents.Add(d);
        }
    }

    private static string Render(IReadOnlyList<RetrievedDocument> docs, string emptyMessage) =>
        docs.Count == 0
            ? emptyMessage
            : string.Join("\n\n", docs.Select(d => $"[SOURCE:{d.Id}] {d.Title}\n{d.Text}"));
}
