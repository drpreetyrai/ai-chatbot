using Aria.Domain;
using Aria.Domain.Accounts;

namespace Aria.Tests;

/// <summary>
/// The account rules, tested where they live rather than through HTTP.
///
/// Two invariants carry the whole access model: an account cannot sign in until an
/// administrator approves it, and approving a clinician or a patient without linking
/// them to a real record is not allowed. The second is the one that is easy to lose in
/// a refactor, because nothing visibly breaks — until an identity exists that maps to
/// no patient, and every downstream check starts answering a question nobody asked.
/// </summary>
public class AccountTests
{
    private static UserAccount Account(UserRole role, AccountStatus status = AccountStatus.Pending) => new()
    {
        Id = "acc-1",
        TenantId = "northbridge",
        Email = "someone@northbridge.health",
        DisplayName = "Someone",
        Role = role,
        Status = status,
        PasswordSalt = "c2FsdA==",
        PasswordHash = "aGFzaA==",
    };

    [Theory]
    [InlineData(AccountStatus.Pending, "approval")]
    [InlineData(AccountStatus.Rejected, "not approved")]
    [InlineData(AccountStatus.Suspended, "suspended")]
    public void An_unapproved_account_cannot_sign_in_and_says_why(AccountStatus status, string expected)
    {
        var account = Account(UserRole.Clinician, status);

        Assert.False(account.CanSignIn(out var reason));
        Assert.Contains(expected, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_approved_account_can_sign_in()
    {
        var account = Account(UserRole.Clinician, AccountStatus.Approved);

        Assert.True(account.CanSignIn(out _));
    }

    [Fact]
    public void Approving_a_clinician_requires_a_linked_clinician_record()
    {
        var account = Account(UserRole.Clinician);

        var error = Assert.Throws<InvalidOperationException>(
            () => account.Approve("AD-3001", linkedDoctorId: null, linkedPatientId: null, note: null));

        Assert.Contains("clinician record", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AccountStatus.Pending, account.Status);
    }

    [Fact]
    public void Approving_a_patient_requires_a_linked_patient_record()
    {
        var account = Account(UserRole.Patient);

        Assert.Throws<InvalidOperationException>(
            () => account.Approve("AD-3001", linkedDoctorId: null, linkedPatientId: null, note: null));

        // The account is left exactly as it was. A half-approved account — approved but
        // unlinked — is the state this rule exists to make unreachable.
        Assert.Equal(AccountStatus.Pending, account.Status);
    }

    [Fact]
    public void Approval_records_who_decided_and_when()
    {
        var account = Account(UserRole.Clinician);

        account.Approve("AD-3001", linkedDoctorId: "DR-1042", linkedPatientId: null, note: "GMC verified");

        Assert.Equal(AccountStatus.Approved, account.Status);
        Assert.Equal("DR-1042", account.LinkedDoctorId);
        Assert.Equal("AD-3001", account.ReviewedBy);
        Assert.NotNull(account.ReviewedAt);

        // The note is the evidence. "Someone approved this" without a reason is not an
        // audit trail, it is a timestamp.
        Assert.Equal("GMC verified", account.ReviewNote);
    }
}

/// <summary>
/// Who may see whose record.
///
/// This is one method because it needs to be one method. The cross-patient leak that
/// prompted it was five endpoints each deciding for themselves, and four of them being
/// right.
/// </summary>
public class PatientAccessTests
{
    private static ClinicianIdentity Identity(UserRole role, string? patientId = null) =>
        new("northbridge", "DR-1042", "Someone", "someone@northbridge.health", "Cardiology", role)
        {
            PatientId = patientId,
        };

    [Fact]
    public void A_patient_may_read_their_own_record()
    {
        Assert.True(Identity(UserRole.Patient, "pt-john").MayAccessPatient("pt-john"));
    }

    [Fact]
    public void A_patient_may_not_read_anyone_elses()
    {
        Assert.False(Identity(UserRole.Patient, "pt-john").MayAccessPatient("pt-sarah"));
    }

    [Fact]
    public void A_patient_account_with_no_link_may_read_nothing()
    {
        // Belt and braces: approval refuses to create this state, and if one ever existed
        // it must default to seeing nothing rather than to seeing everything.
        Assert.False(Identity(UserRole.Patient).MayAccessPatient("pt-john"));
    }

    [Fact]
    public void A_clinician_may_read_the_patients_of_their_clinic()
    {
        Assert.True(Identity(UserRole.Clinician).MayAccessPatient("pt-sarah"));
    }

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Coordinator)]
    [InlineData(UserRole.Auditor)]
    public void Non_clinical_roles_may_not_read_patient_records(UserRole role)
    {
        Assert.False(Identity(role).MayAccessPatient("pt-john"));
    }

    [Fact]
    public void An_administrator_never_sees_phi()
    {
        // The single most load-bearing line in the RBAC matrix: an admin configures and
        // audits the system and has no clinical access at all.
        Assert.False(Identity(UserRole.Admin).MayViewPhi);
    }
}
