using Aria.Domain;

namespace Aria.Agents.Tools;

/// <summary>
/// Declared authority and role permissions for every tool in the system.
///
/// This table is the single source of truth that <see cref="Middleware.ToolAuthorizationMiddleware"/>
/// enforces. A tool that is not in this table cannot be called at all — an unknown tool name is
/// treated as a denial, not as a default-allow.
/// </summary>
public sealed record ToolDescriptor(
    string Name,
    ToolAuthority Authority,
    UserRole[] AllowedRoles,
    string Purpose);

public static class ToolCatalog
{
    private static readonly UserRole[] Clinical    = [UserRole.Clinician, UserRole.ClinicalSafetyOfficer];
    private static readonly UserRole[] ClinicalOps = [UserRole.Clinician, UserRole.Coordinator, UserRole.ClinicalSafetyOfficer];

    public static readonly IReadOnlyDictionary<string, ToolDescriptor> All =
        new[]
        {
            // ── Read ── nothing here can change the world.
            new ToolDescriptor("get_encounter_transcript",  ToolAuthority.Read, Clinical,    "Read the transcript of the current encounter"),
            new ToolDescriptor("get_patient_summary",       ToolAuthority.Read, Clinical,    "Allergies, conditions, active medications, recent history"),
            new ToolDescriptor("search_patient_record",     ToolAuthority.Read, Clinical,    "Retrieval scoped to one patient's signed records"),
            new ToolDescriptor("search_guidelines",         ToolAuthority.Read, Clinical,    "Retrieval over the tenant's pinned guideline pack"),
            new ToolDescriptor("get_guideline_section",     ToolAuthority.Read, Clinical,    "Fetch a guideline section by id, for citation"),
            new ToolDescriptor("lookup_patient_allergies",  ToolAuthority.Read, Clinical,    "Recorded allergies for the patient in context"),
            new ToolDescriptor("check_allergy_conflict",    ToolAuthority.Read, Clinical,    "Deterministic contraindication check — authoritative"),
            new ToolDescriptor("check_drug_interactions",   ToolAuthority.Read, Clinical,    "Deterministic interaction check"),
            new ToolDescriptor("get_note_template",         ToolAuthority.Read, Clinical,    "Department note template"),
            new ToolDescriptor("get_freebusy",              ToolAuthority.Read, ClinicalOps, "Doctor's free/busy from the calendar of record"),
            new ToolDescriptor("get_availability_rules",    ToolAuthority.Read, ClinicalOps, "Clinic hours, slot length, buffer policy"),
            new ToolDescriptor("get_approved_templates",    ToolAuthority.Read, ClinicalOps, "Approved patient-message templates"),
            new ToolDescriptor("check_service_window",      ToolAuthority.Read, ClinicalOps, "Remaining WhatsApp 24-hour service window"),

            // ── Draft ── produces something a human will look at. Still no external effect.
            new ToolDescriptor("suggest_icd_codes",         ToolAuthority.Draft, Clinical,    "Coding suggestions, each tied to a note span"),
            new ToolDescriptor("propose_slots",             ToolAuthority.Draft, ClinicalOps, "At most three slot proposals, each with a reason"),
            new ToolDescriptor("render_template",           ToolAuthority.Draft, ClinicalOps, "Fill an approved template's parameters"),

            // ── Hold ── reserves something, reversibly, with a TTL.
            new ToolDescriptor("hold_slot",                 ToolAuthority.Hold,  ClinicalOps, "Reserve an Aria-held slot for 15 minutes"),
            new ToolDescriptor("cancel_hold",               ToolAuthority.Hold,  ClinicalOps, "Release a hold"),

            // ── Commit ── changes the world. Unreachable outside Aria.Workers (Invariant 1).
            new ToolDescriptor("book_slot",                 ToolAuthority.Commit, ClinicalOps, "Write a booking to the calendar of record"),
            new ToolDescriptor("send_message",              ToolAuthority.Commit, ClinicalOps, "Send a message to a patient"),
            new ToolDescriptor("write_ehr_document",        ToolAuthority.Commit, Clinical,    "Write a DocumentReference to the EHR"),
        }
        .ToDictionary(d => d.Name, StringComparer.Ordinal);

    public static ToolDescriptor? Find(string name) => All.GetValueOrDefault(name);

    /// <summary>
    /// Authorities legal at each lifecycle stage. Commit before signature is the case the whole
    /// design exists to make impossible, so it is stated here as data rather than as a comment.
    /// </summary>
    public static bool IsLegalAt(ToolAuthority authority, Runtime.AgentLifecycle lifecycle) =>
        lifecycle switch
        {
            Runtime.AgentLifecycle.PreSignature  => authority is ToolAuthority.Read or ToolAuthority.Draft or ToolAuthority.Hold,
            Runtime.AgentLifecycle.PostSignature => true,
            _ => false,
        };
}
