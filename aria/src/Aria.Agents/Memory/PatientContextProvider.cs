using System.Text;
using Aria.Agents.Runtime;
using Aria.Domain;
using Aria.Domain.Notes;
using Aria.Infrastructure.Persistence;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;

namespace Aria.Agents.Memory;

/// <summary>
/// Long-term EPISODIC memory: what we know about this patient, injected as structured context.
///
/// Three properties make this safe rather than merely useful:
///
///   1. Every line carries a source id, so a claim the model makes from memory can still be
///      cited. Memory that cannot be cited is memory that cannot be trusted.
///
///   2. source="signed_records_only" is literal — the query filters on signed status. An unsigned
///      draft from ten minutes ago is invisible here. Nothing a human has not signed becomes
///      something the system "remembers".
///
///   3. It is capped and deterministically ordered, allergies first. When it must truncate it
///      truncates the oldest history, never the safety-critical facts at the top.
/// </summary>
public sealed class PatientContextProvider(AriaDbContext db, AgentContext context) : AIContextProvider
{
    private const int MaxRecentNotes = 5;

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext ctx, CancellationToken cancellationToken)
    {
        if (context.PatientId is null) return new AIContext();

        var patient = await db.Patients.AsNoTracking()
            .Include(p => p.Flags)
            .FirstOrDefaultAsync(p => p.Id == context.PatientId && p.TenantId == context.TenantId, cancellationToken);

        if (patient is null) return new AIContext();

        var recentNotes = await db.Notes.AsNoTracking()
            .Include(n => n.Sections)
            .Where(n => n.PatientId == patient.Id
                     && n.TenantId == context.TenantId
                     && n.Status == NoteStatus.Signed)     // ← the gate, in the query itself
            .OrderByDescending(n => n.SignedAt)
            .Take(MaxRecentNotes)
            .ToListAsync(cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine($"<patient_context patient_id=\"{patient.Id}\" as_of=\"{DateTimeOffset.UtcNow:O}\" source=\"signed_records_only\">");

        // Allergies first, always. If anything is truncated it will not be this.
        var allergies = patient.Flags.Where(f => f.Kind is FlagKind.Allergy).ToList();
        sb.AppendLine(allergies.Count == 0
            ? "  ALLERGIES:    none recorded"
            : "  ALLERGIES:    " + string.Join("; ", allergies.Select(a =>
                  $"{a.Label} ({a.Severity.ToString().ToLowerInvariant()}, {a.RecordedAt:yyyy-MM-dd}, {a.SourceRef ?? "no source"})")));

        var conditions = patient.Flags.Where(f => f.Kind is FlagKind.Condition).ToList();
        if (conditions.Count > 0)
            sb.AppendLine("  CONDITIONS:   " + string.Join("; ", conditions.Select(c =>
                $"{c.Label} ({c.RecordedAt:yyyy-MM-dd}, {c.SourceRef ?? "no source"})")));

        sb.AppendLine($"  DEMOGRAPHICS: {patient.AgeYears(DateOnly.FromDateTime(DateTime.Today))} y, {patient.Sex}");

        if (recentNotes.Count > 0)
        {
            sb.AppendLine("  RECENT:");
            foreach (var n in recentNotes)
            {
                var gist = n.Sections.FirstOrDefault(s => s.Kind is NoteSectionKind.Assessment)?.Text
                        ?? n.Sections.FirstOrDefault()?.Text
                        ?? "(no content)";
                sb.AppendLine($"                {n.SignedAt:yyyy-MM-dd} — {Truncate(gist, 90)} (note#{n.Id})");
            }
        }

        sb.AppendLine("</patient_context>");
        sb.AppendLine("Facts above come from signed records and may be cited by their source id.");

        return new AIContext { Instructions = sb.ToString() };
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max - 1), "…");
}
