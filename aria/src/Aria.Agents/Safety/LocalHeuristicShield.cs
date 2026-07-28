using System.Text.RegularExpressions;
using Aria.Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace Aria.Agents.Safety;

/// <summary>
/// Pattern-based injection detection used when Azure AI Content Safety is not configured.
///
/// Honest about what it is: it catches the instruction-shaped text in our red-team corpus and the
/// published technique families, and it will miss a determined novel attack. Production requires
/// the real service, which is why <see cref="Aria.Shared.Configuration.AriaConfigurationExtensions"/>
/// refuses to start a Production stamp without it.
///
/// Note the design choice: detection removes the document from context rather than failing the
/// request. Availability is preserved; the attacker's payload simply never reaches the model.
/// </summary>
public sealed partial class LocalHeuristicShield(ILogger<LocalHeuristicShield> logger) : IPromptShield
{
    public string Name => "local-heuristic-v2";

    private static readonly (Regex Pattern, string Family)[] Signatures =
    [
        (New(@"ignore (all )?(previous|prior|above|earlier) (instructions|prompts|rules)"), "instruction_override"),
        (New(@"disregard (the )?(above|previous|prior|system)"),                            "instruction_override"),
        (New(@"forget (everything|all|your) (you|instructions|rules|training)"),            "instruction_override"),
        (New(@"you are (now|actually) (a|an|in) "),                                         "persona_hijack"),
        (New(@"(new|updated) (instructions|system prompt|rules)\s*:"),                      "instruction_injection"),
        (New(@"</?(system|instruction|untrusted_content)>"),                                "delimiter_escape"),
        (New(@"\[\[?\s*(system|admin|developer)\s*\]?\]"),                                  "delimiter_escape"),
        (New(@"(reveal|print|show|repeat|output) (me )?(your |the )?(system )?(prompt|instructions|rules)"), "prompt_extraction"),
        (New(@"do not (tell|inform|mention to) the (doctor|clinician|user)"),               "concealment"),
        (New(@"(without|don'?t) (asking|requiring|needing) (for )?(approval|permission|confirmation)"), "approval_bypass"),
        (New(@"(book|schedule|send|prescribe|order|cancel)\b.{0,40}?(immediately|right now|automatically|on my behalf)"), "unauthorised_action"),
        // \b rather than a literal space: real payloads write "add:" and "record --", not "add ".
        // Assuming a separator is exactly the kind of gap an attacker finds and a test catches.
        (New(@"(add|record|write|put|note)\b.{0,40}?(no allerg|not allergic|allergy.{0,15}(none|cleared|removed))"), "record_falsification"),
        (New(@"as (an? )?(admin|administrator|doctor|clinician|nurse)\b.{0,10}?(i|we)\s+(authorise|authorize|approve|permit)"), "authority_spoof"),
        (New(@"(this|the) (message|document) (is|was) (approved|authorised|signed) by"),    "authority_spoof"),
        (New(@"override (the )?(safety|guardrail|policy|restriction)"),                     "guardrail_bypass"),
        (New(@"repeat after me\s*:"),                                                       "echo_attack"),
        (New(@"\bBEGIN\s+(SYSTEM|ADMIN|OVERRIDE)\b"),                                       "delimiter_escape"),
    ];

    /// <summary>
    /// Zero-width and bidirectional-override characters. These are how a payload is hidden from
    /// the human reviewing a document while remaining perfectly visible to the model.
    /// Written as explicit escapes so the intent survives a copy-paste through any editor.
    /// </summary>
    private static readonly Regex Invisible = New(
        "[\u200B-\u200F"    // zero-width space .. right-to-left mark
      + "\u202A-\u202E"     // bidi embedding / override
      + "\u2060-\u2064"     // word joiner .. invisible plus
      + "\uFEFF]");            // zero-width no-break space

    public Task<ShieldVerdict> ScanAsync(
        string? userPrompt, IReadOnlyList<RetrievedDocument> documents, CancellationToken ct = default)
    {
        string? evidence = null;

        var promptFamily = userPrompt is null ? null : Detect(userPrompt);
        if (promptFamily is not null)
        {
            evidence = promptFamily;
            logger.LogWarning("Prompt injection detected in user prompt: {Family}", promptFamily);
        }

        var attacked = new List<string>();
        foreach (var doc in documents)
        {
            // Trusted content is our own guideline corpus, ingested through a governed pipeline.
            if (doc.Trust == Domain.TrustLevel.Trusted) continue;

            if (Detect(doc.Text) is { } docFamily)
            {
                attacked.Add(doc.Id);
                evidence ??= docFamily;
                logger.LogWarning("Indirect injection detected in {DocId}: {Family}", doc.Id, docFamily);
            }
        }

        return Task.FromResult(new ShieldVerdict(promptFamily is not null, attacked, Name, evidence));
    }

    private static string? Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var cleaned = Invisible.Replace(text, "");
        if (cleaned.Length != text.Length) return "invisible_characters";

        foreach (var (pattern, family) in Signatures)
            if (pattern.IsMatch(cleaned)) return family;

        return null;
    }

    private static Regex New(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
