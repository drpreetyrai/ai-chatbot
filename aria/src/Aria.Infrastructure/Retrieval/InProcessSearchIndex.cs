using System.Text.RegularExpressions;
using Aria.Domain;
using Aria.Domain.Contracts;
using Aria.Domain.Notes;
using Aria.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aria.Infrastructure.Retrieval;

/// <summary>
/// Hybrid lexical retrieval that runs with no external dependency, so the product's retrieval
/// guardrails — patient scoping, citation resolution, "show nothing rather than guess" — are
/// fully exercised before anyone provisions Azure AI Search.
///
/// Scoring is BM25-flavoured: term frequency with a length penalty, plus a phrase bonus. It is
/// not a vector index and does not pretend to be; when SEARCH_ENDPOINT is set, the Azure-backed
/// implementation replaces it and the semantics stay identical.
/// </summary>
public sealed partial class InProcessSearchIndex(AriaDbContext db) : ISearchIndex
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    { "the","a","an","of","for","to","in","on","is","are","was","were","and","or","with","has","have","had",
      "he","she","it","his","her","this","that","been","before","did","do","does","what","when","any" };

    public async Task<IReadOnlyList<RetrievedDocument>> SearchGuidelinesAsync(
        string query, string packVersion, string? specialty, int topK, CancellationToken ct = default)
    {
        var docs = await db.Guidelines.AsNoTracking()
            .Where(g => g.PackVersion == packVersion)
            .Where(g => specialty == null || g.Specialty == specialty || g.Specialty == "general")
            .ToListAsync(ct);

        return Rank(query, docs.Select(g => new RetrievedDocument(
                   g.Id, $"{g.Title} ({g.Section})", g.Text,
                   TrustLevel.Trusted, g.Citation, g.Url)), topK);
    }

    public async Task<IReadOnlyList<RetrievedDocument>> SearchPatientRecordAsync(
        string query, string tenantId, string patientId, int topK, CancellationToken ct = default)
    {
        // ── The security filter. Applied here, server-side, from the authenticated context. ──
        // Note there is no code path that lets a caller pass a different patientId than the one
        // the tool layer bound. Widening the scope is not "discouraged", it is unreachable.
        var notes = await db.Notes.AsNoTracking()
            .Include(n => n.Sections)
            .Where(n => n.TenantId == tenantId
                     && n.PatientId == patientId
                     && n.Status == NoteStatus.Signed)   // ← signed records only (plan.md §5.3)
            .OrderByDescending(n => n.SignedAt)
            .Take(50)
            .ToListAsync(ct);

        var docs = notes.Select(n => new RetrievedDocument(
            Id: $"note#{n.Id}",
            Title: $"Note · {n.SignedAt:dd MMM yyyy}",
            Text: string.Join("\n", n.Sections.OrderBy(s => s.Kind)
                                              .Select(s => $"{s.Kind.ToString().ToUpperInvariant()}: {s.Text}")),
            Trust: TrustLevel.UntrustedRetrieved,   // ← prior notes may contain injected text
            Citation: $"note {n.SignedAt:dd MMM yyyy}")).ToList();

        // Deliberately strict. "No lexical match" must keep meaning "the record does not
        // answer this", because that is what the clinical Q&A surface reports as
        // insufficient evidence — and a search that always returns its best guess would
        // turn that honest refusal into a confident answer from an unrelated note.
        // Conversational surfaces that want recent context ask for it explicitly, below.
        return Rank(query, docs, topK);
    }

    /// <summary>
    /// The patient's most recent signed visits, in date order, with no query at all.
    ///
    /// This exists because conversation is not search. A patient asks "what did the doctor
    /// say was wrong with me?" — which shares no vocabulary with their own note ("chest
    /// infection, right lower lobe") — and a clinician asks "what happened last time?".
    /// Neither has terms to match on, and both are asking about the same obvious thing.
    ///
    /// Kept separate from <see cref="SearchPatientRecordAsync"/> so that the strict
    /// contract there stays strict.
    /// </summary>
    public async Task<IReadOnlyList<RetrievedDocument>> RecentVisitsAsync(
        string tenantId, string patientId, int take, CancellationToken ct = default)
    {
        var notes = await db.Notes.AsNoTracking()
            .Include(n => n.Sections)
            .Where(n => n.TenantId == tenantId
                     && n.PatientId == patientId
                     && n.Status == NoteStatus.Signed)
            .OrderByDescending(n => n.SignedAt)
            .Take(take)
            .ToListAsync(ct);

        return [.. notes.Select(n => new RetrievedDocument(
            Id: $"note#{n.Id}",
            Title: $"Note · {n.SignedAt:dd MMM yyyy}",
            Text: string.Join("\n", n.Sections.OrderBy(s => s.Kind)
                                              .Select(s => $"{s.Kind.ToString().ToUpperInvariant()}: {s.Text}")),
            Trust: TrustLevel.UntrustedRetrieved,
            Citation: $"note {n.SignedAt:dd MMM yyyy}"))];
    }

    public async Task<RetrievedDocument?> GetGuidelineAsync(string id, CancellationToken ct = default)
    {
        var g = await db.Guidelines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return g is null
            ? null
            : new RetrievedDocument(g.Id, $"{g.Title} ({g.Section})", g.Text, TrustLevel.Trusted, g.Citation, g.Url);
    }

    private static IReadOnlyList<RetrievedDocument> Rank(
        string query, IEnumerable<RetrievedDocument> candidates, int topK)
    {
        var terms = Tokenise(query);
        if (terms.Length == 0) return [];

        var scored = new List<RetrievedDocument>();

        foreach (var doc in candidates)
        {
            var haystack = $"{doc.Title}\n{doc.Text}";
            var tokens = Tokenise(haystack);
            if (tokens.Length == 0) continue;

            double score = 0;
            foreach (var term in terms)
            {
                var tf = tokens.Count(t => t.Equals(term, StringComparison.OrdinalIgnoreCase));
                if (tf == 0)
                {
                    // Partial credit for stems: "breathless" should reach "breathlessness".
                    tf = tokens.Count(t => t.StartsWith(term, StringComparison.OrdinalIgnoreCase)
                                        || term.StartsWith(t, StringComparison.OrdinalIgnoreCase)) > 0 ? 1 : 0;
                    score += tf * 0.4;
                }
                else
                {
                    score += tf / (tf + 1.2 * (0.25 + 0.75 * tokens.Length / 120.0));
                }
            }

            // Phrase bonus: an exact multi-word hit is worth far more than the sum of its terms.
            if (terms.Length > 1 && haystack.Contains(string.Join(' ', terms), StringComparison.OrdinalIgnoreCase))
                score *= 1.8;

            if (score > 0) scored.Add(doc with { Score = Math.Round(score, 4) });
        }

        return scored.OrderByDescending(d => d.Score).Take(topK).ToList();
    }

    private static string[] Tokenise(string text) =>
        [.. NonWord().Split(text.ToLowerInvariant())
             .Where(t => t.Length > 2 && !StopWords.Contains(t))];

    [GeneratedRegex(@"[^\w]+")] private static partial Regex NonWord();
}
