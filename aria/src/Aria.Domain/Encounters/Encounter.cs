namespace Aria.Domain.Encounters;

public sealed class Encounter
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string PatientId { get; init; }
    public required string DoctorId { get; init; }
    public required string Department { get; init; }

    public EncounterState State { get; set; } = EncounterState.Scheduled;
    public string? ConsentId { get; set; }
    public string? Room { get; set; }
    public string? ChiefComplaint { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Doctor-dropped bookmarks. One tap, no typing (wireframe S-03 "Mark moment").</summary>
    public List<long> MarkedMomentsMs { get; init; } = [];

    public TimeSpan Duration =>
        StartedAt is null ? TimeSpan.Zero : (EndedAt ?? DateTimeOffset.UtcNow) - StartedAt.Value;
}

/// <summary>
/// Consent is a visible object with its own lifecycle, not a checkbox buried in settings
/// (wireframe §9.8). Capture is blocked while pending, and declined is a first-class outcome:
/// the doctor can still work manually.
/// </summary>
public sealed class Consent
{
    public required string Id { get; init; }
    public required string EncounterId { get; init; }
    public required string CapturedBy { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
    public string Method { get; init; } = "verbal";
    public bool Granted { get; init; }
    public string RetentionStatement { get; init; } =
        "Audio processed in-region and not retained after the note is drafted.";
}

public sealed class TranscriptSegment
{
    public required string Id { get; init; }
    public required string EncounterId { get; init; }
    /// <summary>"Dr." or "Pt." — from diarisation. Correctable by the clinician, hence settable.</summary>
    public required string Speaker { get; set; }
    public required string Text { get; init; }
    public required long StartMs { get; init; }
    public required long EndMs { get; init; }
    /// <summary>Word-level ASR confidence. Low values become low-confidence note spans.</summary>
    public double Confidence { get; init; } = 1.0;
    public bool IsFinal { get; init; } = true;
}
