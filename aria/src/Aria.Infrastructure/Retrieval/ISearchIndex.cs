using Aria.Domain;
using Aria.Domain.Contracts;

namespace Aria.Infrastructure.Retrieval;

/// <summary>
/// Retrieval, with the security filter as a REQUIRED constructor-style parameter rather than an
/// optional argument. The scope a query runs in is decided by the caller's authenticated context,
/// never by the model — that is why <c>patientId</c> and <c>tenantId</c> are separate parameters
/// here and are not part of any tool schema the model can see (plan.md §4.1 rule 2).
/// </summary>
public interface ISearchIndex
{
    Task<IReadOnlyList<RetrievedDocument>> SearchGuidelinesAsync(
        string query, string packVersion, string? specialty, int topK, CancellationToken ct = default);

    /// <summary>Patient-scoped. The filter is applied server-side and cannot be widened.</summary>
    Task<IReadOnlyList<RetrievedDocument>> SearchPatientRecordAsync(
        string query, string tenantId, string patientId, int topK, CancellationToken ct = default);

    /// <summary>
    /// Recent signed visits for a patient, by date, ignoring the query entirely.
    /// For conversational surfaces, where the question rarely shares vocabulary with the
    /// answer. Search stays strict so "insufficient evidence" keeps its meaning.
    /// </summary>
    Task<IReadOnlyList<RetrievedDocument>> RecentVisitsAsync(
        string tenantId, string patientId, int take, CancellationToken ct = default);

    Task<RetrievedDocument?> GetGuidelineAsync(string id, CancellationToken ct = default);
}
