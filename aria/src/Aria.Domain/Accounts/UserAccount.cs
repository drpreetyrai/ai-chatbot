namespace Aria.Domain.Accounts;

/// <summary>
/// Where a registration is in its life.
///
/// <see cref="Pending"/> is the important one: an account exists but cannot sign in.
/// A clinical system must not let someone self-serve their way to patient data, so
/// registration and access are deliberately two separate events with a human in
/// between (the same "draft until a human signs" shape as the rest of the product).
/// </summary>
public enum AccountStatus { Pending, Approved, Rejected, Suspended }

/// <summary>
/// A sign-in identity. Distinct from <see cref="ClinicianIdentity"/>, which is the
/// clinical tuple the rest of the system keys on: an account is how you get in, and
/// what it is <em>linked to</em> determines what you can see.
/// </summary>
public sealed class UserAccount
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }

    /// <summary>Stored lower-cased; uniqueness is enforced case-insensitively.</summary>
    public required string Email { get; init; }
    public required string DisplayName { get; set; }

    public required string PasswordHash { get; set; }
    public required string PasswordSalt { get; set; }

    public required UserRole Role { get; init; }
    public AccountStatus Status { get; set; } = AccountStatus.Pending;

    /// <summary>What the caller said about themselves at sign-up, shown to the approving admin.</summary>
    public string? RequestedReason { get; init; }
    public string? Department { get; init; }
    public string? Phone { get; init; }

    /// <summary>
    /// The clinical record this account speaks for.
    ///
    /// A doctor account links to a <c>ClinicianRecord</c>; a patient account links to a
    /// <c>Patient</c>. Linking happens at approval, not at sign-up — otherwise anyone
    /// could claim to be a given patient simply by typing their MRN.
    /// </summary>
    public string? LinkedDoctorId { get; set; }
    public string? LinkedPatientId { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }
    public DateTimeOffset? LastSignInAt { get; set; }

    /// <summary>
    /// Only an approved account may sign in. Rejected and suspended accounts are told
    /// they cannot, rather than being told the password was wrong — a security-theatre
    /// lie that just generates support tickets.
    /// </summary>
    public bool CanSignIn(out string? reason)
    {
        reason = Status switch
        {
            AccountStatus.Pending   => "Your account is awaiting approval by an administrator.",
            AccountStatus.Rejected  => "This registration was not approved. Contact the practice if you believe that is wrong.",
            AccountStatus.Suspended => "This account has been suspended. Contact an administrator.",
            _ => null,
        };
        return reason is null;
    }

    /// <summary>
    /// Approval is the moment access is granted, so it is the moment the account is
    /// bound to a clinical record. A doctor without a linked clinician record, or a
    /// patient without a linked patient record, could sign in and see nothing — or
    /// worse, be treated as unscoped.
    /// </summary>
    public void Approve(string reviewerId, string? linkedDoctorId, string? linkedPatientId, string? note)
    {
        if (Role is UserRole.Clinician && string.IsNullOrWhiteSpace(linkedDoctorId))
            throw new InvalidOperationException("A clinician account must be linked to a clinician record on approval.");

        if (Role is UserRole.Patient && string.IsNullOrWhiteSpace(linkedPatientId))
            throw new InvalidOperationException("A patient account must be linked to a patient record on approval.");

        Status = AccountStatus.Approved;
        LinkedDoctorId = linkedDoctorId;
        LinkedPatientId = linkedPatientId;
        ReviewedBy = reviewerId;
        ReviewedAt = DateTimeOffset.UtcNow;
        ReviewNote = note;
    }

    public void Reject(string reviewerId, string? note)
    {
        Status = AccountStatus.Rejected;
        ReviewedBy = reviewerId;
        ReviewedAt = DateTimeOffset.UtcNow;
        ReviewNote = note;
    }
}

/// <summary>
/// An issued session.
///
/// Sessions are rows rather than self-contained tokens precisely so that sign-out
/// means something: revoking a row takes effect on the next request. A stateless JWT
/// cannot be withdrawn, and "sign out" that leaves a valid token in the wild is not
/// sign-out — it is clearing a browser field.
/// </summary>
public sealed class UserSession
{
    public required string Id { get; init; }
    public required string AccountId { get; init; }

    /// <summary>SHA-256 of the bearer token. The token itself is never stored.</summary>
    public required string TokenHash { get; init; }

    public DateTimeOffset IssuedAt { get; init; } = DateTimeOffset.UtcNow;
    public required DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? UserAgent { get; init; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}
