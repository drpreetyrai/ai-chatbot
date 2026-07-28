namespace Aria.Domain;

/// <summary>
/// Identity tuple the entire system keys on (wireframe S-01, S-10).
/// Resolved once at sign-in; carries the correct calendar and messaging sender with it.
/// </summary>
/// <param name="TenantId">Tenancy boundary. Enforced again at the database by row-level security.</param>
public sealed record ClinicianIdentity(
    string TenantId,
    string DoctorId,
    string Name,
    string Email,
    string Department,
    UserRole Role,
    string? GoogleCalendarId = null,
    string? WhatsAppSenderId = null)
{
    /// <summary>Set for a patient account: the one record they may see, and only that one.</summary>
    public string? PatientId { get; init; }

    public bool IsClinician => Role is UserRole.Clinician;
    public bool IsPatient => Role is UserRole.Patient;
    public bool CanSign => Role is UserRole.Clinician;

    /// <summary>
    /// Clinical staff see PHI across their patients. A patient sees PHI too — but only
    /// their own, which is enforced separately by <see cref="PatientId"/> scoping.
    /// Admins configure and audit and never see PHI at all (plan.md §10.1).
    /// </summary>
    public bool MayViewPhi => Role is UserRole.Clinician or UserRole.ClinicalSafetyOfficer or UserRole.Patient;

    /// <summary>
    /// True when this identity may read the given patient's record.
    ///
    /// The check is centralised because "am I allowed to see this patient" is asked from
    /// a dozen endpoints, and a single missed check is a cross-patient data leak.
    /// </summary>
    public bool MayAccessPatient(string patientId) => Role switch
    {
        UserRole.Patient => string.Equals(PatientId, patientId, StringComparison.Ordinal),
        UserRole.Clinician or UserRole.ClinicalSafetyOfficer => true,
        _ => false,
    };
}
