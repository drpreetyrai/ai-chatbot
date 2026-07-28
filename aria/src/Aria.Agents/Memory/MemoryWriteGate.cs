using Aria.Domain;
using Aria.Domain.Notes;
using Aria.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aria.Agents.Memory;

/// <summary>
/// The only writer to long-term memory.
///
/// It accepts exactly two triggers — a signed note, and an explicit clinician save. A draft, a
/// rejected suggestion, or an unapproved message never becomes something the system remembers.
///
/// This is a governance control before it is an engineering one: it means the answer to "why does
/// Aria think my patient is allergic to X?" is always a document a human put their name to.
/// </summary>
public sealed class MemoryWriteGate(
    AriaDbContext db,
    IClinicianPreferenceStore preferences,
    ILogger<MemoryWriteGate> logger)
{
    /// <summary>
    /// Called from the post-signature workflow. Refuses anything unsigned, loudly — a caller
    /// trying to persist a draft into long-term memory is a bug worth failing on.
    /// </summary>
    public async Task OnNoteSignedAsync(Note note, CancellationToken ct = default)
    {
        if (note.Status is not NoteStatus.Signed)
            throw new InvalidOperationException(
                $"MemoryWriteGate refused note {note.Id}: status is {note.Status}, not Signed. " +
                "Long-term memory accepts signed records only.");

        // 1 — The note becomes retrievable. Indexing is driven by signature, never by draft.
        logger.LogInformation("Note {NoteId} admitted to long-term memory (signed by {Signer}).",
            note.Id, note.SignedBy);

        // 2 — Learn the clinician's style from what they actually changed.
        await LearnFromEditsAsync(note, ct);
    }

    /// <summary>
    /// Procedural memory, learned from the draft→signed diff rather than a preferences screen.
    ///
    /// Only strong, repeated signals are learned. A single edit is noise; a clinician who rewrites
    /// the same section every time is telling us something.
    /// </summary>
    private async Task LearnFromEditsAsync(Note note, CancellationToken ct)
    {
        var edited = note.AllSpans.Where(s => s.EditedByHuman).ToList();
        if (edited.Count == 0) return;

        var doctorId = note.SignedBy ?? note.DoctorId;

        var worstSection = note.Sections
            .Select(s => (s.Kind, Edits: s.Spans.Count(sp => sp.EditedByHuman)))
            .Where(x => x.Edits > 0)
            .OrderByDescending(x => x.Edits)
            .FirstOrDefault();

        if (worstSection.Edits >= 2)
        {
            await preferences.LearnAsync(doctorId,
                kind: $"rewrite_hotspot.{worstSection.Kind.ToString().ToLowerInvariant()}",
                value: $"This clinician frequently rewrites the {worstSection.Kind} section — be more conservative there.",
                learnedFrom: $"note#{note.Id}",
                ct);

            logger.LogInformation("Learned style signal for {DoctorId}: {Section} rewritten {Count}x.",
                doctorId, worstSection.Kind, worstSection.Edits);
        }

        // Template preference is a simple frequency signal and needs no cleverness.
        var templateUse = await db.Notes.AsNoTracking()
            .Where(n => n.DoctorId == doctorId && n.Status == NoteStatus.Signed)
            .GroupBy(n => n.TemplateId)
            .Select(g => new { Template = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .FirstOrDefaultAsync(ct);

        if (templateUse is { Count: >= 5 })
            await preferences.LearnAsync(doctorId, "preferred_template", templateUse.Template,
                $"{templateUse.Count} signed notes", ct);
    }
}
