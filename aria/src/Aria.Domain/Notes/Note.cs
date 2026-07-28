namespace Aria.Domain.Notes;

/// <summary>
/// A clinical note. Mutable while <see cref="NoteStatus.Draft"/>; permanently frozen on signature.
/// After signing, corrections are addenda with their own audit trail — never silent edits.
/// Visual permanence in the UI mirrors this legal permanence (wireframe S-04).
/// </summary>
public sealed class Note
{
    public required string Id { get; init; }
    public required string EncounterId { get; init; }
    public required string TenantId { get; init; }
    public required string PatientId { get; init; }
    public required string DoctorId { get; init; }

    public string TemplateId { get; set; } = "consultation.general";
    public NoteStatus Status { get; private set; } = NoteStatus.Draft;

    /// <summary>Recorded on every note and every audit row — you can always answer "which model wrote this?".</summary>
    public string? ModelVersion { get; set; }
    public string? PromptVersion { get; set; }

    public List<NoteSection> Sections { get; init; } = [];
    public List<AttachedAction> AttachedActions { get; init; } = [];
    public List<CodeSuggestion> Codes { get; init; } = [];
    public List<NoteAddendum> Addenda { get; init; } = [];

    public DateTimeOffset DraftCreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SignedAt { get; private set; }
    public string? SignedBy { get; private set; }
    public string? SignatureHash { get; private set; }

    /// <summary>
    /// Shown to the clinician, not just to analytics (wireframe S-04). A quiet quality contract:
    /// we are measuring how wrong we were.
    /// </summary>
    public double EditDistance { get; set; }

    /// <summary>Set when the model failed and we fell back to transcript + entities only.</summary>
    public bool DraftUnavailable { get; set; }

    public IEnumerable<NoteSpan> AllSpans => Sections.SelectMany(s => s.Spans);

    public int LowConfidenceSpanCount =>
        AllSpans.Count(s => s.Band is ConfidenceBand.Low && !s.AcceptedByHuman);

    /// <summary>
    /// A note cannot be signed while low-confidence spans remain unreviewed. This is the
    /// mechanism behind "Low confidence forces an explicit accept or rewrite" (plan.md §6.2).
    /// </summary>
    public bool IsSignable(out string? blocker)
    {
        if (Status is not NoteStatus.Draft and not NoteStatus.AwaitingSignature)
        { blocker = $"Note is {Status.ToString().ToLowerInvariant()} and can no longer be signed."; return false; }

        if (LowConfidenceSpanCount > 0)
        { blocker = $"{LowConfidenceSpanCount} low-confidence passage(s) still need your explicit accept or rewrite."; return false; }

        blocker = null;
        return true;
    }

    public void Sign(string signedBy, string signatureHash)
    {
        if (!IsSignable(out var blocker)) throw new InvalidOperationException(blocker);
        Status = NoteStatus.Signed;
        SignedBy = signedBy;
        SignedAt = DateTimeOffset.UtcNow;
        SignatureHash = signatureHash;
    }

    public void Discard()
    {
        if (Status is NoteStatus.Signed)
            throw new InvalidOperationException("A signed note is immutable. Add an addendum instead.");
        Status = NoteStatus.Discarded;
    }

    public void AddAddendum(NoteAddendum addendum)
    {
        if (Status is not NoteStatus.Signed)
            throw new InvalidOperationException("Addenda apply only to signed notes; edit the draft directly.");
        Addenda.Add(addendum);
    }

    internal void RestoreStatus(NoteStatus status, string? signedBy, DateTimeOffset? signedAt, string? hash)
    { Status = status; SignedBy = signedBy; SignedAt = signedAt; SignatureHash = hash; }
}

public sealed class NoteSection
{
    public required string Id { get; init; }
    public required NoteSectionKind Kind { get; init; }
    public List<NoteSpan> Spans { get; init; } = [];
    public string Text => string.Join(" ", Spans.Select(s => s.Text));
}

/// <summary>
/// The unit of provenance. Every sentence the model writes is a span that points back at the
/// transcript window it came from. A span with no source is deleted before render — that is
/// what makes "show your work" structural rather than a promise (wireframe §9.2).
/// </summary>
public sealed class NoteSpan
{
    public required string Id { get; init; }

    /// <summary>
    /// Position within the section. Owned collections have no inherent order in EF, and a Plan
    /// whose numbered steps come back shuffled is worse than no plan at all.
    /// </summary>
    public int Ordinal { get; init; }

    public required string Text { get; set; }
    public long? TranscriptStartMs { get; init; }
    public long? TranscriptEndMs { get; init; }
    public double Confidence { get; init; } = 1.0;
    public bool AcceptedByHuman { get; set; }
    public bool EditedByHuman { get; set; }
    /// <summary>Why the model was unsure — shown verbatim in the provenance panel.</summary>
    public string? FlagReason { get; init; }

    public bool HasProvenance => TranscriptStartMs is not null && TranscriptEndMs is not null;

    public ConfidenceBand Band => Confidence >= 0.85 ? ConfidenceBand.High
                                : Confidence >= 0.65 ? ConfidenceBand.Medium
                                : ConfidenceBand.Low;
}

/// <summary>
/// The checkboxes on the Note Review screen. One signature updates five systems — and this
/// is the one place that stops all five (wireframe S-04).
/// </summary>
public sealed class AttachedAction
{
    public required string Id { get; init; }
    public required OutboxActionType Kind { get; init; }
    public required string Description { get; init; }
    public bool Enabled { get; set; } = true;
    public string PayloadJson { get; set; } = "{}";
    /// <summary>Set by a deterministic safety check; a blocked action cannot be re-enabled by the UI.</summary>
    public string? BlockedReason { get; set; }
}

public sealed class CodeSuggestion
{
    public required string Code { get; init; }
    public required string System { get; init; }
    public required string Display { get; init; }
    /// <summary>The note span that justifies this code. No justification, no suggestion.</summary>
    public string? JustifyingSpanId { get; init; }
    public double Confidence { get; init; }
}

public sealed class NoteAddendum
{
    public required string Id { get; init; }
    public required string NoteId { get; init; }
    public required string AuthorId { get; init; }
    public required string Body { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
