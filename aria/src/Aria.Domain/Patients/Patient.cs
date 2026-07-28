namespace Aria.Domain.Patients;

public sealed class Patient
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string Mrn { get; init; }
    public required string Name { get; init; }
    public required DateOnly DateOfBirth { get; init; }
    public required string Sex { get; init; }
    public string? Phone { get; init; }
    public string PreferredLanguage { get; init; } = "en";

    public List<PatientFlag> Flags { get; init; } = [];

    public int AgeYears(DateOnly today) =>
        today.Year - DateOfBirth.Year - (today < DateOfBirth.AddYears(today.Year - DateOfBirth.Year) ? 1 : 0);

    public IEnumerable<PatientFlag> Allergies => Flags.Where(f => f.Kind is FlagKind.Allergy);

    /// <summary>
    /// Phone numbers and MRNs are masked by default; revealing them is an audited action
    /// (wireframe §9.9, PHI minimisation).
    /// </summary>
    public string MaskedPhone => Phone is null or { Length: < 4 }
        ? "—"
        : string.Concat(Phone.AsSpan(0, 3), "••••••", Phone.AsSpan(Phone.Length - 3));
}

public sealed class PatientFlag
{
    public required string Id { get; init; }
    public required string PatientId { get; init; }
    public required FlagKind Kind { get; init; }
    /// <summary>Normalised code (e.g. RxNorm ingredient) so the allergy checker can match deterministically.</summary>
    public required string Code { get; init; }
    public required string Label { get; init; }
    public FlagSeverity Severity { get; init; } = FlagSeverity.Moderate;
    /// <summary>Where this came from — a note id, a lab id. Everything is traceable.</summary>
    public string? SourceRef { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
}
