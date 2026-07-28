using Aria.Domain;

namespace Aria.Infrastructure.Persistence;

/// <summary>
/// The identity tuple, persisted. In production this is projected from Entra ID claims; locally
/// the dev identity provider issues tokens against these rows so RBAC is exercised either way.
/// </summary>
public sealed class ClinicianRecord
{
    public required string DoctorId { get; init; }
    public required string TenantId { get; init; }
    public required string FacilityId { get; init; }
    public required string Name { get; init; }
    public required string Email { get; init; }
    public required string Department { get; init; }
    public required UserRole Role { get; init; }
    /// <summary>Set when the clinician completes Google consent; bookings are written here.</summary>
    public string? GoogleCalendarId { get; set; }
    public string? WhatsAppSenderId { get; init; }
    public bool CalendarConnected { get; set; }
    public string Status { get; init; } = "active";

    public ClinicianIdentity ToIdentity() =>
        new(TenantId, DoctorId, Name, Email, Department, Role, GoogleCalendarId, WhatsAppSenderId);
}

/// <summary>
/// A guideline section. Version-pinned per tenant: a note signed in March must still resolve its
/// citation in July, so old pack versions stay queryable forever (plan.md §5.5).
/// </summary>
public sealed class GuidelineDocument
{
    public required string Id { get; init; }
    public required string PackVersion { get; init; }
    public required string Publisher { get; init; }
    public required string Title { get; init; }
    public required string Section { get; init; }
    public required string Text { get; init; }
    public required string Citation { get; init; }
    public string? Url { get; init; }
    public string Specialty { get; init; } = "general";
}

/// <summary>
/// One turn of an assistant conversation.
///
/// Persisted rather than kept in memory because a conversation that forgets when the
/// tab is closed is not a conversation, and because a patient-facing exchange about
/// their care is part of the record — it must be auditable after the fact.
/// </summary>
public sealed class AssistantTurnRecord
{
    public required string Id { get; init; }
    public required string ConversationId { get; init; }
    public required string TenantId { get; init; }
    public string? PatientId { get; init; }
    public required string ActorId { get; init; }

    /// <summary>"user" or "assistant".</summary>
    public required string Role { get; init; }
    public required string Text { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A clinician's authorisation to write to their own Google Calendar.
///
/// Per clinician, never a shared service account: events appear under their identity,
/// and they can revoke Aria from their Google account without anyone's help.
///
/// The refresh token is stored here for local development. In production it belongs in
/// Key Vault, encrypted per clinician (plan.md §15) — and it is never returned by any
/// endpoint under any circumstances.
/// </summary>
public sealed class CalendarConnection
{
    public required string DoctorId { get; init; }
    public required string TenantId { get; init; }
    public required string CalendarId { get; set; }
    public required string RefreshToken { get; set; }
    public DateTimeOffset ConnectedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A busy block pulled from a clinician's real Google Calendar.
///
/// Cached rather than read live, for a reason that is about safety rather than latency:
/// the API is not allowed to call external systems (only the workers are), and a
/// scheduling screen that fails open — showing a doctor as free because a third party
/// timed out — would double-book them. A cached block with a visible <c>SyncedAt</c>
/// degrades honestly; a missing one does not.
///
/// These rows are a projection, never a source of truth. The sync worker replaces a
/// clinician's whole window on every pass, so deletions in Google propagate.
/// </summary>
public sealed class ExternalCalendarBlock
{
    /// <summary>Doctor id and Google's event id, so a re-sync updates rather than duplicates.</summary>
    public required string Id { get; init; }
    public required string DoctorId { get; init; }
    public required string TenantId { get; init; }
    public required DateTimeOffset StartAt { get; set; }
    public required DateTimeOffset EndAt { get; set; }
    public required string Title { get; set; }
    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;
}
