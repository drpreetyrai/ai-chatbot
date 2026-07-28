namespace Aria.Infrastructure.Persistence;

/// <summary>
/// The audit vocabulary. Fixed strings because an auditor filters on them and a typo would
/// silently hide events from a compliance export.
/// </summary>
public static class AuditActions
{
    public const string SignIn             = "SIGN_IN";
    public const string EncounterStarted   = "ENCOUNTER_STARTED";
    public const string EncounterEnded     = "ENCOUNTER_ENDED";
    public const string ConsentCaptured    = "CONSENT_CAPTURED";
    public const string ConsentDeclined    = "CONSENT_DECLINED";
    public const string DraftGenerated     = "DRAFT_GENERATED";
    public const string DraftDegraded      = "DRAFT_DEGRADED";
    public const string NoteEdited         = "NOTE_EDITED";
    public const string SpanAccepted       = "SPAN_ACCEPTED";
    public const string SpanRejected       = "SPAN_REJECTED";
    public const string NoteSigned         = "SIGNED";
    public const string NoteDiscarded      = "NOTE_DISCARDED";
    public const string AddendumAdded      = "ADDENDUM_ADDED";
    public const string SuggestionRejected = "REJECTED_SUGGESTION";
    public const string OutboxDispatched   = "OUTBOX_DISPATCHED";
    public const string OutboxFailed       = "OUTBOX_FAILED";
    public const string MessageApproved    = "MESSAGE_APPROVED";
    public const string MessageSent        = "MESSAGE_SENT";
    public const string MessageUndone      = "MESSAGE_UNDONE";
    public const string Escalation         = "ESCALATION";
    public const string EscalationAck      = "ESCALATION_ACKNOWLEDGED";
    public const string GuardrailBlocked   = "GUARDRAIL_BLOCKED";
    public const string ToolDenied         = "TOOL_DENIED";
    public const string AutonomyChanged    = "AUTONOMY_CHANGED";
    public const string AutonomyRefused    = "AUTONOMY_CHANGE_REFUSED";
    public const string PhiUnmasked        = "PHI_UNMASKED";
    public const string BreakGlass         = "BREAK_GLASS";
    public const string SlotHeld           = "SLOT_HELD";
    public const string SlotBooked         = "SLOT_BOOKED";
    public const string FeedbackReported   = "BAD_SUGGESTION_REPORTED";
}
