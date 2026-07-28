namespace Aria.Domain.Governance;

/// <summary>
/// The transactional outbox. Invariant 1 lives here: <see cref="NoteId"/> is non-nullable and the
/// database carries a CHECK constraint to match. No signed note, no external write. Ever.
/// </summary>
public sealed class OutboxItem
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    /// <summary>Non-nullable by design. This is the write barrier (plan.md §1.2, Invariant 1).</summary>
    public required string NoteId { get; init; }
    public required OutboxActionType ActionType { get; init; }
    public required string PayloadJson { get; init; }
    /// <summary>{noteId}:{actionType}:{attemptGroup} — makes every external call safely retryable.</summary>
    public required string IdempotencyKey { get; init; }

    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public string? ExternalRef { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? VisibleAfter { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public bool IsReady(DateTimeOffset now) =>
        Status is OutboxStatus.Pending && (VisibleAfter is null || VisibleAfter <= now);
}

/// <summary>
/// Written for the auditor, not the developer: who, what, which patient, which model version,
/// how many human edits. Rows are hash-chained so tampering is detectable (plan.md §10.2).
/// </summary>
public sealed class AuditEntry
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public required string ActorId { get; init; }
    public required ActorKind ActorKind { get; init; }
    public required string Action { get; init; }
    public string? TargetKind { get; init; }
    public string? TargetId { get; init; }
    public string? PatientId { get; init; }
    public string? ModelVersion { get; init; }
    public string? PromptVersion { get; init; }
    public int? HumanEdits { get; init; }
    public string Outcome { get; init; } = "ok";
    public string DetailJson { get; init; } = "{}";

    /// <summary>SHA-256 of the previous row. Breaks visibly if any earlier row is altered.</summary>
    public string PrevHash { get; set; } = string.Empty;
    public string RowHash { get; set; } = string.Empty;

    /// <summary>Deterministic canonical form. Order matters — never reorder these fields.</summary>
    public string CanonicalPayload() => string.Join('|',
        Id, TenantId, Timestamp.ToUnixTimeMilliseconds().ToString(), ActorId, ActorKind.ToString(),
        Action, TargetKind ?? "", TargetId ?? "", PatientId ?? "", ModelVersion ?? "",
        PromptVersion ?? "", HumanEdits?.ToString() ?? "", Outcome, DetailJson, PrevHash);
}

/// <summary>
/// Autonomy is a per-department, per-intent dial — Paediatrics and Orthopaedics do not carry the
/// same risk. Red-flag escalation is hard-wired to human and cannot be changed (wireframe S-10).
/// </summary>
public sealed class AutonomySetting
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    /// <summary>"tenant" | "facility" | "department". Settings inherit downward.</summary>
    public required string ScopeKind { get; init; }
    public required string ScopeId { get; init; }
    public required string Intent { get; init; }
    public AutonomyMode Mode { get; set; } = AutonomyMode.Draft;
    public string? ApprovedBy { get; set; }
    /// <summary>Promotions are time-boxed and auto-revert to Draft unless re-approved (plan.md §10.4).</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>
/// Clinician corrections are the highest-value training signal the product has, so
/// "Report bad suggestion" writes straight into the evaluation funnel (wireframe §9.10).
/// </summary>
public sealed class Feedback
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string Surface { get; init; }
    public string? TargetId { get; init; }
    public required string DoctorId { get; init; }
    public required string Reason { get; init; }
    public string? Detail { get; init; }
    public string EvalCandidateStatus { get; init; } = "new";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
