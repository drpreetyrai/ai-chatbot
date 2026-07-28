using System.Net.Http.Json;
using System.Text.Json;
using Aria.Domain.Contracts;
using Aria.Shared.Configuration;
using Microsoft.Extensions.Logging;

namespace Aria.Agents.Safety;

/// <summary>
/// Azure AI Content Safety Prompt Shields. Scans the user prompt and every untrusted document in
/// one call, which is what the API is designed for.
///
/// Failure policy is fail-closed but bounded: if the service is unreachable we fall back to the
/// local heuristic rather than either (a) letting everything through, or (b) taking the clinic
/// offline. A degraded shield is logged loudly and surfaced on the Safety dashboard.
/// </summary>
public sealed class AzureContentSafetyShield(
    HttpClient http,
    AriaOptions options,
    LocalHeuristicShield fallback,
    ILogger<AzureContentSafetyShield> logger) : IPromptShield
{
    public string Name => "azure-content-safety-prompt-shields";

    public async Task<ShieldVerdict> ScanAsync(
        string? userPrompt, IReadOnlyList<RetrievedDocument> documents, CancellationToken ct = default)
    {
        var untrusted = documents.Where(d => d.Trust != Domain.TrustLevel.Trusted).ToList();

        try
        {
            var endpoint = options.ContentSafety.Endpoint!.TrimEnd('/');
            var url = $"{endpoint}/contentsafety/text:shieldPrompt?api-version=2024-09-01";

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new
                {
                    userPrompt = userPrompt ?? string.Empty,
                    documents = untrusted.Select(d => d.Text).ToArray(),
                }),
            };
            request.Headers.Add("Ocp-Apim-Subscription-Key", options.ContentSafety.ApiKey);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            var promptAttack = root.TryGetProperty("userPromptAnalysis", out var upa)
                            && upa.TryGetProperty("attackDetected", out var ad) && ad.GetBoolean();

            var attackedIds = new List<string>();
            if (root.TryGetProperty("documentsAnalysis", out var da) && da.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var item in da.EnumerateArray())
                {
                    if (i < untrusted.Count
                        && item.TryGetProperty("attackDetected", out var flag)
                        && flag.GetBoolean())
                        attackedIds.Add(untrusted[i].Id);
                    i++;
                }
            }

            if (promptAttack || attackedIds.Count > 0)
                logger.LogWarning("Prompt Shields flagged: prompt={Prompt} documents={Docs}",
                    promptAttack, string.Join(",", attackedIds));

            return new ShieldVerdict(promptAttack, attackedIds, Name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Content Safety unreachable — falling back to the local heuristic shield. " +
                "Safety posture is DEGRADED until this recovers.");

            var degraded = await fallback.ScanAsync(userPrompt, documents, ct).ConfigureAwait(false);
            return degraded with { Detector = $"{fallback.Name} (degraded from {Name})" };
        }
    }
}
