using Aria.Domain;
using Aria.Domain.Accounts;
using Aria.Domain.Encounters;
using Aria.Domain.Governance;
using Aria.Domain.Messaging;
using Aria.Domain.Patients;
using Aria.Domain.Scheduling;
using Aria.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aria.Infrastructure.Seed;

/// <summary>
/// Seeds the clinic from wireframe.md so the running product matches the specification
/// screen for screen. Same people, same times, same allergy, same red flag.
///
/// This is also the fixture the end-to-end smoke test runs against, which is why it is
/// deterministic: fixed ids, fixed clock offsets from "today".
/// </summary>
public static class DemoSeeder
{
    public const string TenantId   = "northbridge";
    public const string FacilityId = "northbridge-main";
    public const string DrMaya     = "DR-1042";
    public const string DrIyer     = "DR-1058";
    public const string Coordinator= "ST-2210";
    public const string Admin      = "AD-3001";

    public static async Task SeedAsync(AriaDbContext db, CancellationToken ct = default)
    {
        // Accounts are seeded separately, and separately guarded. A database created before
        // sign-in existed already has clinicians, so a single "is anything here?" check would
        // skip the bootstrap admin forever — leaving a working app that nobody can log into.
        await SeedAccountsAsync(db, ct);

        if (await db.Clinicians.AnyAsync(ct)) return;

        var today = DateOnly.FromDateTime(DateTime.Now);
        var day   = DateTime.Today;
        DateTimeOffset At(int h, int m) => new(day.AddHours(h).AddMinutes(m), TimeSpan.FromHours(5.5));

        // ── The team (wireframe S-10) ──
        db.Clinicians.AddRange(
            new ClinicianRecord { DoctorId = DrMaya, TenantId = TenantId, FacilityId = FacilityId,
                Name = "Dr. Maya Rao", Email = "maya.rao@northbridge.health", Department = "Cardiology",
                Role = UserRole.Clinician, GoogleCalendarId = "maya.rao@northbridge.health",
                WhatsAppSenderId = "northbridge-main", CalendarConnected = true },
            new ClinicianRecord { DoctorId = DrIyer, TenantId = TenantId, FacilityId = FacilityId,
                Name = "Dr. A. Iyer", Email = "a.iyer@northbridge.health", Department = "Paediatrics",
                Role = UserRole.Clinician, GoogleCalendarId = "a.iyer@northbridge.health", CalendarConnected = true },
            new ClinicianRecord { DoctorId = Coordinator, TenantId = TenantId, FacilityId = FacilityId,
                Name = "Ravi Kumar", Email = "ravi.k@northbridge.health", Department = "Front desk",
                Role = UserRole.Coordinator },
            new ClinicianRecord { DoctorId = Admin, TenantId = TenantId, FacilityId = FacilityId,
                Name = "Priya Nair", Email = "priya.n@northbridge.health", Department = "Administration",
                Role = UserRole.Admin });

        // ── Patients. John's penicillin allergy is the spine of the whole demo. ──
        var john = new Patient { Id = "pt-john", TenantId = TenantId, Mrn = "44192", Name = "John Abraham",
            DateOfBirth = today.AddYears(-34), Sex = "M", Phone = "+919876543210", PreferredLanguage = "en" };
        john.Flags.AddRange([
            new PatientFlag { Id = "fl-john-pen", PatientId = john.Id, Kind = FlagKind.Allergy,
                Code = "penicillin", Label = "Penicillin allergy", Severity = FlagSeverity.Severe,
                SourceRef = "note#4412", RecordedAt = DateTimeOffset.UtcNow.AddYears(-2) },
            new PatientFlag { Id = "fl-john-asthma", PatientId = john.Id, Kind = FlagKind.Condition,
                Code = "asthma", Label = "Asthma", Severity = FlagSeverity.Moderate,
                SourceRef = "note#2201", RecordedAt = DateTimeOffset.UtcNow.AddYears(-3) },
            new PatientFlag { Id = "fl-john-smoke", PatientId = john.Id, Kind = FlagKind.Lifestyle,
                Code = "non-smoker", Label = "Non-smoker", Severity = FlagSeverity.Info,
                RecordedAt = DateTimeOffset.UtcNow.AddYears(-3) }]);

        var sarah = new Patient { Id = "pt-sarah", TenantId = TenantId, Mrn = "44201", Name = "Sarah Menon",
            DateOfBirth = today.AddYears(-58), Sex = "F", Phone = "+919876500771" };
        sarah.Flags.Add(new PatientFlag { Id = "fl-sarah-htn", PatientId = sarah.Id, Kind = FlagKind.Condition,
            Code = "hypertension", Label = "Hypertension", Severity = FlagSeverity.Moderate,
            SourceRef = "note#5510", RecordedAt = DateTimeOffset.UtcNow.AddYears(-4) });

        var ali = new Patient { Id = "pt-ali", TenantId = TenantId, Mrn = "44210", Name = "Ali Rahman",
            DateOfBirth = today.AddYears(-45), Sex = "M", Phone = "+919876500412" };
        var neha = new Patient { Id = "pt-neha", TenantId = TenantId, Mrn = "44222", Name = "Neha Kapoor",
            DateOfBirth = today.AddYears(-39), Sex = "F", Phone = "+919876500990" };
        var vikram = new Patient { Id = "pt-vikram", TenantId = TenantId, Mrn = "44233", Name = "Vikram Singh",
            DateOfBirth = today.AddYears(-52), Sex = "M", Phone = "+919876500334" };
        vikram.Flags.Add(new PatientFlag { Id = "fl-vik-nsaid", PatientId = vikram.Id, Kind = FlagKind.Allergy,
            Code = "nsaid", Label = "NSAID sensitivity", Severity = FlagSeverity.Moderate,
            RecordedAt = DateTimeOffset.UtcNow.AddYears(-1) });

        db.Patients.AddRange(john, sarah, ali, neha, vikram);

        // ── Today's clinic (wireframe S-02) ──
        db.Encounters.AddRange(
            new Encounter { Id = "enc-john", TenantId = TenantId, PatientId = john.Id, DoctorId = DrMaya,
                Department = "Cardiology", State = EncounterState.CheckedIn, Room = "Room 3",
                ChiefComplaint = "Fever ×3 days, dry cough" },
            new Encounter { Id = "enc-sarah", TenantId = TenantId, PatientId = sarah.Id, DoctorId = DrMaya,
                Department = "Cardiology", State = EncounterState.Scheduled, ChiefComplaint = "Follow-up · HTN" },
            new Encounter { Id = "enc-ali", TenantId = TenantId, PatientId = ali.Id, DoctorId = DrMaya,
                Department = "Cardiology", State = EncounterState.Scheduled, ChiefComplaint = "New · chest pain" });

        db.Appointments.AddRange(
            new Appointment { Id = "ap-1", TenantId = TenantId, PatientId = sarah.Id, DoctorId = DrMaya,
                StartAt = At(10, 20), Reason = "Follow-up · HTN", Status = "confirmed" },
            new Appointment { Id = "ap-2", TenantId = TenantId, PatientId = ali.Id, DoctorId = DrMaya,
                StartAt = At(10, 35), Reason = "New · chest pain", Status = "confirmed" },
            new Appointment { Id = "ap-3", TenantId = TenantId, PatientId = neha.Id, DoctorId = DrMaya,
                StartAt = At(11, 0), Reason = "Report review", Status = "confirmed" },
            new Appointment { Id = "ap-4", TenantId = TenantId, PatientId = john.Id, DoctorId = DrMaya,
                StartAt = At(10, 5), Reason = "Consultation · fever, cough", Status = "confirmed" });

        // ── WhatsApp threads (wireframe S-07). Sarah's question is the approval-queue demo. ──
        var now = DateTimeOffset.UtcNow;
        db.Threads.AddRange(
            new MessageThread { Id = "th-sarah", TenantId = TenantId, PatientId = sarah.Id,
                Status = ThreadStatus.NeedsApproval, ServiceWindowExpiresAt = now.AddHours(6).AddMinutes(19) },
            new MessageThread { Id = "th-neha", TenantId = TenantId, PatientId = neha.Id,
                Status = ThreadStatus.Resolved, ServiceWindowExpiresAt = now.AddHours(14) },
            new MessageThread { Id = "th-ali", TenantId = TenantId, PatientId = ali.Id,
                Status = ThreadStatus.Resolved, ServiceWindowExpiresAt = now.AddHours(20) },
            new MessageThread { Id = "th-vikram", TenantId = TenantId, PatientId = vikram.Id,
                Status = ThreadStatus.Open, ServiceWindowExpiresAt = now.AddHours(23) });

        db.Messages.AddRange(
            new Message { Id = "msg-s1", ThreadId = "th-sarah", Direction = MessageDirection.Outbound,
                Body = "Hi Sarah — reminder: appointment tomorrow at 10:20 with Dr. Maya Rao. Reply RESCHEDULE to change.",
                TemplateId = "appointment_reminder_v3", Status = MessageStatus.Delivered,
                CreatedAt = now.AddHours(-16), SentAt = now.AddHours(-16) },
            new Message { Id = "msg-s2", ThreadId = "th-sarah", Direction = MessageDirection.Inbound,
                Body = "Should I take my BP tablet before coming?", Status = MessageStatus.Delivered,
                CreatedAt = now.AddMinutes(-25), Trust = TrustLevel.UntrustedPatientMessage },
            new Message { Id = "msg-n1", ThreadId = "th-neha", Direction = MessageDirection.Inbound,
                Body = "Can I eat before the blood test?", Status = MessageStatus.Delivered,
                CreatedAt = now.AddMinutes(-46), Trust = TrustLevel.UntrustedPatientMessage },
            new Message { Id = "msg-n2", ThreadId = "th-neha", Direction = MessageDirection.Outbound,
                Body = "Yes — a CBC and CRP don't need fasting. Eat and drink normally. For anything urgent, call the clinic on 080-4000-4400.",
                TemplateId = "clinical_qa_v2", Status = MessageStatus.Delivered, ApprovedBy = DrMaya,
                CreatedAt = now.AddMinutes(-44), SentAt = now.AddMinutes(-44) });

        // ── Approved templates. Patient-facing generation can only ever fill these blanks. ──
        db.MessageTemplates.AddRange(
            new MessageTemplate { Id = "appointment_reminder_v3", TenantId = TenantId, Intent = "appointment_reminder",
                Language = "en", Parameters = ["patient_name", "datetime", "doctor_name"],
                Body = "Hi {{patient_name}} — reminder: appointment on {{datetime}} with {{doctor_name}}. Reply RESCHEDULE to change." },
            new MessageTemplate { Id = "post_visit_summary_v3", TenantId = TenantId, Intent = "post_visit_summary",
                Language = "en", Parameters = ["patient_name", "doctor_name", "summary_points", "safety_netting", "review_datetime"],
                Body = "Hi {{patient_name}} — here's a summary from your visit with {{doctor_name}} today.\n\n{{summary_points}}\n\n{{safety_netting}}\n\nReview: {{review_datetime}}\nReviewed by {{doctor_name}}." },
            new MessageTemplate { Id = "clinical_qa_v2", TenantId = TenantId, Intent = "clinical_qa",
                Language = "en", Parameters = ["answer", "clinic_phone"],
                Body = "{{answer}}\n\nFor anything urgent, call the clinic on {{clinic_phone}}." },
            new MessageTemplate { Id = "reschedule_offer_v1", TenantId = TenantId, Intent = "reschedule_offer",
                Language = "en", Parameters = ["patient_name", "options"],
                Body = "Hi {{patient_name}} — here are the next available times:\n{{options}}\n\nReply with the number you'd like." });

        // ── Autonomy dials (wireframe S-10). Note what is NOT here: red_flag_escalation. ──
        // It is absent on purpose — AutonomyPolicy hard-codes it to AlwaysHuman regardless of data.
        db.AutonomySettings.AddRange(
            new AutonomySetting { Id = "au-1", TenantId = TenantId, ScopeKind = "department", ScopeId = "Cardiology",
                Intent = "appointment_reminder", Mode = AutonomyMode.Auto, ApprovedBy = Admin,
                ExpiresAt = now.AddDays(180) },
            new AutonomySetting { Id = "au-2", TenantId = TenantId, ScopeKind = "department", ScopeId = "Cardiology",
                Intent = "post_visit_summary", Mode = AutonomyMode.Draft },
            new AutonomySetting { Id = "au-3", TenantId = TenantId, ScopeKind = "department", ScopeId = "Cardiology",
                Intent = "reschedule_offer", Mode = AutonomyMode.Draft },
            new AutonomySetting { Id = "au-4", TenantId = TenantId, ScopeKind = "department", ScopeId = "Cardiology",
                Intent = "clinical_qa", Mode = AutonomyMode.Draft });

        db.Guidelines.AddRange(GuidelinePack.Sections());

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The bootstrap accounts.
    ///
    /// Exactly one is pre-approved: the administrator. Everyone else registers and waits
    /// for them, which is the workflow the product is built around — seeding approved
    /// doctors would quietly bypass the very gate being demonstrated.
    ///
    /// The password is a well-known bootstrap credential and the sign-in screen says so.
    /// It exists to make the first sign-in possible, not to be a secret.
    /// </summary>
    private static async Task SeedAccountsAsync(AriaDbContext db, CancellationToken ct)
    {
        if (await db.Accounts.AnyAsync(ct)) return;

        db.Accounts.AddRange(
            Account("acc-admin", "admin@northbridge.health", "Priya Nair", UserRole.Admin,
                    AccountStatus.Approved, department: "Administration"),

            // A doctor and a patient already waiting, so the approval queue is not empty
            // on first run and the workflow can be seen immediately.
            Account("acc-maya", "maya.rao@northbridge.health", "Dr. Maya Rao", UserRole.Clinician,
                    AccountStatus.Pending, department: "Cardiology",
                    reason: "Consultant cardiologist, GMC 7712334. Please link to DR-1042."),

            Account("acc-john", "john.abraham@example.com", "John Abraham", UserRole.Patient,
                    AccountStatus.Pending, phone: "+919876543210",
                    reason: "Patient. Claims MRN 44192 — verify before linking."));

        await db.SaveChangesAsync(ct);
    }

    /// <summary>The one bootstrap credential, stated plainly rather than hidden.</summary>
    public const string BootstrapPassword = "AriaAdmin!2026";

    private static UserAccount Account(
        string id, string email, string name, UserRole role, AccountStatus status,
        string? department = null, string? phone = null, string? reason = null)
    {
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);

        return new UserAccount
        {
            Id = id,
            TenantId = TenantId,
            Email = email,
            DisplayName = name,
            Role = role,
            Status = status,
            Department = department,
            Phone = phone,
            RequestedReason = reason,
            PasswordSalt = Convert.ToBase64String(salt),
            PasswordHash = Convert.ToBase64String(
                System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
                    System.Text.Encoding.UTF8.GetBytes(BootstrapPassword), salt,
                    210_000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32)),
            // The admin is linked immediately; the others are linked at approval, which
            // is where a human actually verifies the claim.
            LinkedDoctorId = role is UserRole.Admin ? Admin : null,
        };
    }
}
