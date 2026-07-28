using Aria.Domain.Contracts;

namespace Aria.Agents.Safety;

/// <summary>
/// Scans everything before the model sees it. Two surfaces, because they are genuinely different
/// attacks: the user's own prompt, and content authored by someone else that we retrieved
/// (a patient message, an uploaded lab PDF, a prior note). The second is the one that matters
/// here — most of the text this product reads was not written by its user.
/// </summary>
public interface IPromptShield
{
    Task<ShieldVerdict> ScanAsync(
        string? userPrompt,
        IReadOnlyList<RetrievedDocument> documents,
        CancellationToken ct = default);

    string Name { get; }
}
