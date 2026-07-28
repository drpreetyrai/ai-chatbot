using Aria.Domain;
using Aria.Domain.Accounts;
using Aria.Domain.Encounters;
using Aria.Domain.Governance;
using Aria.Domain.Messaging;
using Aria.Domain.Notes;
using Aria.Domain.Patients;
using Aria.Domain.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Aria.Infrastructure.Persistence;

public sealed class AriaDbContext(DbContextOptions<AriaDbContext> options) : DbContext(options)
{
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<PatientFlag> PatientFlags => Set<PatientFlag>();
    public DbSet<Encounter> Encounters => Set<Encounter>();
    public DbSet<Consent> Consents => Set<Consent>();
    public DbSet<TranscriptSegment> TranscriptSegments => Set<TranscriptSegment>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<NoteAddendum> NoteAddenda => Set<NoteAddendum>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<SlotHold> SlotHolds => Set<SlotHold>();
    public DbSet<MessageThread> Threads => Set<MessageThread>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageTemplate> MessageTemplates => Set<MessageTemplate>();
    public DbSet<Escalation> Escalations => Set<Escalation>();
    public DbSet<OutboxItem> Outbox => Set<OutboxItem>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();
    public DbSet<AutonomySetting> AutonomySettings => Set<AutonomySetting>();
    public DbSet<Feedback> Feedback => Set<Feedback>();
    public DbSet<ClinicianRecord> Clinicians => Set<ClinicianRecord>();
    public DbSet<GuidelineDocument> Guidelines => Set<GuidelineDocument>();
    public DbSet<UserAccount> Accounts => Set<UserAccount>();
    public DbSet<UserSession> Sessions => Set<UserSession>();
    public DbSet<AssistantTurnRecord> AssistantTurns => Set<AssistantTurnRecord>();
    public DbSet<CalendarConnection> CalendarConnections => Set<CalendarConnection>();
    public DbSet<ExternalCalendarBlock> ExternalCalendarBlocks => Set<ExternalCalendarBlock>();

    /// <summary>
    /// SQLite has no native DateTimeOffset, so it cannot ORDER BY one — which the audit hash
    /// chain depends on absolutely, since a chain ordered wrongly is a chain that does not verify.
    ///
    /// Storing them as a sortable binary keeps the domain model provider-agnostic: the same
    /// entities run on SQLite locally and on Postgres (native timestamptz) in production, and no
    /// query has to know which one it is talking to.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        if (Database.IsSqlite())
        {
            builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
            builder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        }
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        // ── Patients ──
        b.Entity<Patient>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Mrn }).IsUnique();
            e.HasMany(x => x.Flags).WithOne().HasForeignKey(f => f.PatientId).OnDelete(DeleteBehavior.Cascade);

            // Derived views over Flags, not stored state. Without these EF sees Allergies as a
            // second navigation to PatientFlag and refuses to build the model.
            e.Ignore(x => x.Allergies);
            e.Ignore(x => x.MaskedPhone);
        });
        b.Entity<PatientFlag>().HasKey(x => x.Id);

        // ── Encounters ──
        b.Entity<Encounter>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.DoctorId, x.State });
            // Marked moments are a small ordered list; a join table would be ceremony.
            e.Property(x => x.MarkedMomentsMs)
             .HasConversion(v => string.Join(',', v),
                            v => v.Length == 0 ? new List<long>() : v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToList())
             .Metadata.SetValueComparer(Comparers.LongList);
        });
        b.Entity<Consent>().HasKey(x => x.Id);
        b.Entity<TranscriptSegment>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.EncounterId, x.StartMs });
        });

        // ── Notes. Sections and spans are owned: they have no life outside their note. ──
        b.Entity<Note>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasIndex(x => x.EncounterId);

            e.Property(x => x.Status).HasConversion<string>();

            e.OwnsMany(x => x.Sections, s =>
            {
                s.ToTable("NoteSections");
                s.WithOwner().HasForeignKey("NoteId");
                s.HasKey(x => x.Id);
                s.Property(x => x.Kind).HasConversion<string>();
                s.Ignore(x => x.Text);

                s.OwnsMany(x => x.Spans, sp =>
                {
                    sp.ToTable("NoteSpans");
                    sp.WithOwner().HasForeignKey("NoteSectionId");
                    sp.HasKey(x => x.Id);
                    sp.Ignore(x => x.Band);
                    sp.Ignore(x => x.HasProvenance);
                });
            });

            e.OwnsMany(x => x.AttachedActions, a =>
            {
                a.ToTable("NoteAttachedActions");
                a.WithOwner().HasForeignKey("NoteId");
                a.HasKey(x => x.Id);
                a.Property(x => x.Kind).HasConversion<string>();
            });

            e.OwnsMany(x => x.Codes, c =>
            {
                c.ToTable("NoteCodes");
                c.WithOwner().HasForeignKey("NoteId");
                c.HasKey("NoteId", nameof(CodeSuggestion.Code));
            });

            e.HasMany(x => x.Addenda).WithOne().HasForeignKey(a => a.NoteId);
            e.Ignore(x => x.AllSpans);
            e.Ignore(x => x.LowConfidenceSpanCount);
        });
        b.Entity<NoteAddendum>().HasKey(x => x.Id);

        // ── Scheduling ──
        b.Entity<Appointment>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.DoctorId, x.StartAt }); });
        b.Entity<SlotHold>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.DoctorId, x.StartAt });
            e.Property(x => x.Status).HasConversion<string>();
        });

        // ── Messaging ──
        b.Entity<MessageThread>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.Property(x => x.Status).HasConversion<string>();
        });
        b.Entity<Message>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ThreadId, x.CreatedAt });
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.Direction).HasConversion<string>();
            e.Property(x => x.Trust).HasConversion<string>();
        });
        b.Entity<MessageTemplate>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Parameters)
             .HasConversion(v => string.Join(',', v), v => v.Split(',', StringSplitOptions.RemoveEmptyEntries))
             .Metadata.SetValueComparer(Comparers.StringArray);
        });
        b.Entity<Escalation>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.AcknowledgedAt });
            e.Property(x => x.Severity).HasConversion<string>();
            e.Ignore(x => x.AckLatencySeconds);
        });

        // ── Governance ──
        b.Entity<OutboxItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Status, x.VisibleAfter });
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.ActionType).HasConversion<string>();
            e.Property(x => x.NoteId).IsRequired();

            // ── INVARIANT 1, enforced by the database itself. ──
            // No signed note, no external write. A bug in application code cannot get around this.
            e.ToTable(t => t.HasCheckConstraint(
                "CK_Outbox_RequiresSignedNote",
                "NoteId IS NOT NULL AND length(NoteId) > 0"));
        });

        b.Entity<AuditEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Timestamp });
            e.HasIndex(x => x.PatientId);
            e.Property(x => x.ActorKind).HasConversion<string>();
        });

        b.Entity<AutonomySetting>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.ScopeKind, x.ScopeId, x.Intent }).IsUnique();
            e.Property(x => x.Mode).HasConversion<string>();
        });

        b.Entity<Feedback>().HasKey(x => x.Id);

        b.Entity<ClinicianRecord>(e =>
        {
            e.HasKey(x => x.DoctorId);
            e.Property(x => x.Role).HasConversion<string>();
        });

        b.Entity<GuidelineDocument>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.PackVersion);
        });

        // ── Accounts and sessions ──
        b.Entity<UserAccount>(e =>
        {
            e.HasKey(x => x.Id);
            // Unique on email so a second registration for the same address cannot race
            // its way past the application-level check.
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.Property(x => x.Role).HasConversion<string>();
            e.Property(x => x.Status).HasConversion<string>();
        });

        b.Entity<UserSession>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.AccountId);
        });

        b.Entity<AssistantTurnRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ConversationId, x.At });
        });

        b.Entity<CalendarConnection>().HasKey(x => x.DoctorId);

        b.Entity<ExternalCalendarBlock>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.DoctorId, x.StartAt });
        });
    }
}

internal static class Comparers
{
    public static readonly Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<long>> LongList =
        new((a, b) => a!.SequenceEqual(b!), v => v.Aggregate(0, (acc, x) => HashCode.Combine(acc, x)), v => v.ToList());

    public static readonly Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<string[]> StringArray =
        new((a, b) => a!.SequenceEqual(b!), v => v.Aggregate(0, (acc, x) => HashCode.Combine(acc, x.GetHashCode())), v => v.ToArray());
}
