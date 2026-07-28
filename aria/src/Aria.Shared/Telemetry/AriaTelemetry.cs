using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Aria.Shared.Telemetry;

/// <summary>
/// Product event names from wireframe §14. These are a contract, not log strings: dashboards,
/// alerts and the autonomy-promotion evidence all key off them, so renaming one is a breaking change.
/// </summary>
public static class AriaEvents
{
    public const string EncounterStarted      = "encounter.started";
    public const string EncounterEnded        = "encounter.ended";
    public const string NoteDraftCompleted    = "note.draft_completed";
    public const string NoteSectionEdited     = "note.section_edited";
    public const string NoteSigned            = "note.signed";

    public const string SuggestionShown       = "ai.suggestion_shown";
    public const string SuggestionAccepted    = "ai.suggestion_accepted";
    public const string SuggestionRejected    = "ai.suggestion_rejected";
    public const string BadSuggestionReported = "ai.bad_suggestion_reported";
    public const string ProvenanceOpened      = "provenance.opened";

    public const string SlotOffered           = "schedule.slot_offered";
    public const string SlotBooked            = "schedule.slot_booked";

    public const string MessageDrafted        = "message.drafted";
    public const string MessageApproved       = "message.approved";
    public const string MessageEdited         = "message.edited";
    public const string MessageSent           = "message.sent";

    public const string EscalationRaised      = "escalation.raised";
    public const string EscalationAcknowledged= "escalation.acknowledged";

    /// <summary>Guardrail interventions. Suffix with the reason, e.g. guardrail.prompt_injection.</summary>
    public const string GuardrailPrefix       = "guardrail.";

    public const string IntegrationFailure    = "integration.failure";

    public const string OnboardingDemoStarted   = "onboarding.demo_started";
    public const string OnboardingDemoCompleted = "onboarding.demo_completed";
    public const string ExampleExecuted         = "example.executed";
}

/// <summary>
/// Baggage every span and event carries (wireframe §14). Set once in middleware so no developer
/// ever has to remember it — the tag that is only added when someone remembers is the tag that
/// is missing from the one trace you needed.
/// </summary>
public static class AriaTags
{
    public const string TenantId      = "aria.tenant_id";
    public const string FacilityId    = "aria.facility_id";
    public const string Department    = "aria.department";
    public const string DoctorId      = "aria.doctor_id";
    public const string EncounterId   = "aria.encounter_id";
    public const string PatientId     = "aria.patient_id";
    public const string NoteId        = "aria.note_id";
    public const string ModelVersion  = "aria.model_version";
    public const string PromptVersion = "aria.prompt_version";
    public const string AgentName     = "aria.agent";
    public const string ToolName      = "aria.tool";
    public const string ToolAuthority = "aria.tool_authority";
    public const string GuardrailKind = "aria.guardrail";
    public const string Outcome       = "aria.outcome";
    public const string LatencyMs     = "aria.latency_ms";
    public const string Confidence    = "aria.confidence";
}

/// <summary>Single source for the ActivitySource and Meter, so instrumentation registration cannot drift.</summary>
public static class AriaDiagnostics
{
    public const string ActivitySourceName = "Aria.Clinical";
    public const string MeterName          = "Aria.Clinical";

    public static readonly ActivitySource Source = new(ActivitySourceName, "1.0.0");
    public static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly Counter<long> Events =
        Meter.CreateCounter<long>("aria.events", "count", "Product events by name");

    public static readonly Counter<long> GuardrailInterventions =
        Meter.CreateCounter<long>("aria.guardrail.interventions", "count", "Guardrail blocks by reason");

    public static readonly Histogram<double> DraftLatency =
        Meter.CreateHistogram<double>("aria.note.draft_latency", "ms", "Encounter close to draft complete");

    public static readonly Histogram<double> EscalationAckLatency =
        Meter.CreateHistogram<double>("aria.escalation.ack_latency", "s", "Escalation raised to acknowledged");

    public static Activity? StartAgent(string agentName) =>
        Source.StartActivity($"agent {agentName}", ActivityKind.Client)?.SetTag(AriaTags.AgentName, agentName);

    public static Activity? StartTool(string toolName, string authority) =>
        Source.StartActivity($"tool {toolName}", ActivityKind.Internal)
              ?.SetTag(AriaTags.ToolName, toolName)
              .SetTag(AriaTags.ToolAuthority, authority);
}
