namespace Aria.Domain;

/// <summary>Encounter lifecycle. Transitions are enforced by <see cref="Encounters.EncounterStateMachine"/>.</summary>
public enum EncounterState { Scheduled, CheckedIn, Recording, Paused, Ended, Drafting, AwaitingSignature, Signed, Abandoned }

/// <summary>A note is provisional until it is signed. Signing is the only write barrier (plan.md §1.2, Invariant 1).</summary>
public enum NoteStatus { Draft, AwaitingSignature, Signed, Discarded }

public enum NoteSectionKind { Subjective, Objective, Assessment, Plan }

/// <summary>
/// Three bands, never a bare decimal (wireframe §8: "Never a bare percentage").
/// Low always renders the verify affordance and cannot be bulk-accepted.
/// </summary>
public enum ConfidenceBand { Low, Medium, High }

/// <summary>What a tool is allowed to do. Commit tools are unreachable outside Aria.Workers.</summary>
public enum ToolAuthority { Read, Draft, Hold, Commit }

public enum ActorKind { Clinician, Coordinator, Admin, Auditor, System, Patient }

/// <summary>
/// Patient is a first-class role, not an afterthought: the patient portal is a real
/// surface with its own scoping rules, and "everyone except patients" is exactly the
/// kind of implicit assumption that leaks data.
/// </summary>
public enum UserRole { Patient, Clinician, Coordinator, Admin, Auditor, ClinicalSafetyOfficer }

public enum FlagKind { Allergy, Condition, Lifestyle }

public enum FlagSeverity { Info, Moderate, Severe }

public enum OutboxActionType { EhrDocumentWrite, PharmacyOrder, LabOrder, CalendarBooking, PatientMessage }

public enum OutboxStatus { Pending, InFlight, Succeeded, Failed, DeadLettered }

public enum MessageDirection { Inbound, Outbound }

public enum MessageStatus { Draft, PendingApproval, Approved, Queued, Sent, Delivered, Failed, Discarded, Quarantined }

public enum ThreadStatus { Open, BotHandled, NeedsApproval, Escalated, Resolved }

public enum EscalationSeverity { RedFlag, Urgent }

public enum AutonomyMode { Draft, Auto, AlwaysHuman }

public enum SlotHoldStatus { Held, Booked, Released, Expired }

/// <summary>
/// Trust level of a piece of text entering model context. Anything not <see cref="Trusted"/>
/// is fenced and can never originate a Draft/Hold/Commit tool call (plan.md §7, D3).
/// </summary>
public enum TrustLevel { Trusted, UntrustedPatientMessage, UntrustedDocument, UntrustedTranscript, UntrustedRetrieved }
