using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Aria.Safety;

/// <summary>
/// The deterministic red-flag term net. Versioned, because a missed escalation must be
/// reproducible against the exact net that was live when it happened.
///
/// Two design biases run through every pattern here:
///
///   • OVER-TRIGGER ON PURPOSE. A false escalation costs a clinician thirty seconds. A missed one
///     is the incident that ends the product. Where the two trade off, this net chooses noise.
///
///   • MATCH HOW FRIGHTENED PEOPLE ACTUALLY TYPE, not how a textbook writes. Real messages carry
///     repeated letters, missing apostrophes, transliterated Hindi, filler words between the noun
///     and the symptom ("my chest feels tight"), and occasionally deliberate obfuscation.
///     Every one of those is a case in the golden set, and several of the patterns below exist
///     because that test caught the plain-English version missing them.
/// </summary>
public sealed partial class KeywordNet
{
    public const string Version = "kw-v3";

    private static readonly (string Pattern, string Trigger)[] Patterns =
    [
        // ── Cardiac ──
        // Up to two filler words are allowed between the noun and the symptom, so
        // "chest feels tight" and "chest is really painful" both land.
        (@"chest\s+(\w+\s+){0,2}(pain|painful|tight|tightness|pressure|heavy|heaviness|discomfort)", "chest_pain"),
        // Both word orders. People say "chest pain" and "pressure in my chest" about
        // equally often, and matching only one of them is a 50% miss rate on the single
        // most important symptom this net exists to catch.
        (@"(pain|pressure|tightness|heaviness|discomfort|ache)\s+(in|around|across)\s+(my\s+|the\s+)?chest", "chest_pain"),
        (@"\bheart\s+(attack|racing|pounding)\b",                            "cardiac"),
        (@"pain\s+(in|down)\s+(my\s+)?(left\s+)?arm",                        "cardiac_radiation"),
        (@"(jaw|shoulder)\s+pain.{0,20}(sweat|clammy)",                      "cardiac_radiation"),
        (@"se+ne?\s+me+i?n\s+dard",                                          "chest_pain_hi"),   // "seene mein dard"
        (@"cha+ti\s+me+i?n\s+dard",                                          "chest_pain_hi"),

        // ── Respiratory ──
        (@"cant\s+breathe|cannot\s+breathe|can\s+not\s+breathe",             "airway"),
        (@"(trouble|difficulty|hard|struggling)\s+breathing",                "dyspnoea"),
        (@"short(ness)?\s+of\s+breath|breathless",                           "dyspnoea"),
        (@"gasping|choking|wheezing\s+badly",                                "airway"),
        (@"sa+ns\s+(nahi|nhi)",                                              "airway_hi"),

        // ── Neurological ──
        (@"worst\s+headache",                                                "thunderclap_headache"),
        (@"sudden.{0,20}(weakness|numbness)",                                "stroke"),
        (@"face\s+(is\s+)?(droop|drooping)",                                 "stroke"),
        // Both word orders: people say "slurred speech" and "speech is slurred".
        (@"slurred\s+speech|speech\s+(is\s+)?slurred",                       "stroke"),
        (@"cant\s+(move|feel)\s+(my\s+)?(arm|leg|side|face)",                "stroke"),
        (@"\bseizure|fitting|convulsion",                                    "seizure"),
        (@"(lost|loss\s+of)\s+consciousness|passed\s+out|fainted",           "syncope"),
        (@"not\s+making\s+sense",                                            "altered_mental_state"),

        // ── Haemorrhage / abdominal ──
        (@"(coughing|vomiting|throwing)\s+up\s+blood",                       "haemorrhage"),
        (@"blood\s+in\s+(my\s+)?(stool|urine|vomit|poo|pee)",                "haemorrhage"),
        (@"bleeding\s+(heavily|a\s+lot|non\s*stop|wont\s+stop)",             "haemorrhage"),
        (@"severe\s+(abdominal|stomach|belly|tummy)\s+pain",                 "acute_abdomen"),

        // ── Obstetric ──
        (@"(pregnan|expecting).{0,30}(bleed|pain|cramp)",                    "obstetric"),
        (@"(baby|foetus|fetus)\s+(is\s+)?(not|isnt|hasnt\s+been)\s+moving",  "obstetric"),

        // ── Sepsis / systemic ──
        (@"(high|very\s+high)\s+fever.{0,20}(rash|stiff\s+neck|confus)",     "sepsis"),
        (@"stiff\s+neck.{0,20}(light|photophob)",                            "meningitis"),
        (@"(lips|fingers|skin)\s+(are\s+|is\s+)?(turning\s+)?blue",          "hypoxia"),

        // ── Self-harm. Always human, always immediately. ──
        (@"(kill|hurt|harm)\s+(myself|my\s+self)",                           "self_harm"),
        (@"(want|going|thinking\s+about)\s+to\s+die",                        "self_harm"),
        (@"end(ing)?\s+(my|it)\s+(life|all)",                                "self_harm"),
        (@"suicid",                                                          "self_harm"),
        (@"overdose(d)?|took\s+(all|too\s+many)\s+(my\s+)?(pills|tablets)",  "overdose"),

        // ── Anaphylaxis ──
        (@"(throat|tongue|face|lips)\s+(is\s+|are\s+)?(swelling|swollen|closing)", "anaphylaxis"),
        (@"allergic\s+reaction",                                             "anaphylaxis"),

        // ── Explicit distress that must never be bot-handled ──
        (@"\bemergency\b|\bambulance\b|call\s+1?0?8\b|\b999\b|\b911\b",      "explicit_emergency"),
        (@"\bdying\b",                                                       "explicit_distress"),
    ];

    private static readonly Regex[] Compiled =
        [.. Patterns.Select(p => new Regex(p.Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled))];

    /// <summary>
    /// Normalises the way an anxious person types.
    ///
    /// Order matters here. Apostrophes are dropped BEFORE punctuation becomes whitespace, so
    /// "can't" collapses to "cant" rather than splitting into "can t". Repeated letters collapse
    /// to a single character, not two, because "chesssst paaaain" must reduce to "chest pain" —
    /// reducing it to "chesst paain" would match nothing and quietly miss a real emergency.
    /// </summary>
    public static string Normalise(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var lowered = input.ToLowerInvariant()
                           .Replace('0', 'o').Replace('1', 'i').Replace('3', 'e')
                           .Replace('4', 'a').Replace('$', 's').Replace('@', 'a');

        // Contractions become single words before punctuation is stripped.
        lowered = lowered.Replace("'", string.Empty)
                         .Replace("’", string.Empty)
                         .Replace("`", string.Empty);

        // Strip diacritics so "dolór" matches "dolor".
        var decomposed = lowered.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);

        var stripped = NonWord().Replace(sb.ToString(), " ");
        var collapsed = Repeats().Replace(stripped, "$1");     // "paaaain" -> "pain"
        return Whitespace().Replace(collapsed, " ").Trim();
    }

    /// <summary>Returns every trigger the text matches. Empty means the net saw nothing.</summary>
    public IReadOnlyList<string> Match(string text)
    {
        var normalised = Normalise(text);
        if (normalised.Length == 0) return [];

        var hits = new List<string>();
        for (var i = 0; i < Compiled.Length; i++)
            if (Compiled[i].IsMatch(normalised))
                hits.Add(Patterns[i].Trigger);

        return hits.Distinct().ToList();
    }

    [GeneratedRegex(@"[^\w\s]")]      private static partial Regex NonWord();
    [GeneratedRegex(@"(\w)\1{2,}")]   private static partial Regex Repeats();
    [GeneratedRegex(@"\s+")]          private static partial Regex Whitespace();
}
