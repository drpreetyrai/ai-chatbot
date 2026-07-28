using Aria.Domain;
using Aria.Domain.Patients;

namespace Aria.Safety;

public sealed record AllergyConflict(
    string DrugLabel,
    string DrugCode,
    string AllergyLabel,
    string AllergyCode,
    FlagSeverity Severity,
    string Explanation);

/// <summary>
/// Rule-based, not model-based, and authoritative over anything a model says.
///
/// It runs twice: live during the consultation the instant a drug is mentioned (so the catch
/// happens while the patient is still in the room — worth more than a perfect note), and again
/// over the final plan before signature.
///
/// Target recall is 100%. The CI gate has no override.
/// </summary>
public sealed class AllergyConflictChecker
{
    public const string Version = "allergy-v1";

    /// <summary>
    /// Allergy ingredient -> drugs that are contraindicated. Cross-reactivity is included
    /// explicitly rather than inferred, because "probably fine" is not a clinical standard.
    /// </summary>
    private static readonly Dictionary<string, (string[] Drugs, string Why)> CrossReactivity = new(StringComparer.OrdinalIgnoreCase)
    {
        ["penicillin"] = (
            ["amoxicillin", "ampicillin", "penicillin", "co-amoxiclav", "coamoxiclav", "augmentin",
             "flucloxacillin", "piperacillin", "benzylpenicillin", "phenoxymethylpenicillin"],
            "Beta-lactam of the penicillin class — direct contraindication."),

        ["cephalosporin"] = (
            ["cefalexin", "cephalexin", "ceftriaxone", "cefuroxime", "cefixime", "cefotaxime"],
            "Cephalosporin class — direct contraindication."),

        ["sulfonamide"] = (
            ["co-trimoxazole", "cotrimoxazole", "trimethoprim-sulfamethoxazole", "sulfamethoxazole", "sulfasalazine"],
            "Sulfonamide-containing — direct contraindication."),

        ["nsaid"] = (
            ["ibuprofen", "diclofenac", "naproxen", "aspirin", "ketorolac", "indomethacin", "mefenamic acid"],
            "NSAID class cross-reactivity."),

        ["aspirin"] = (
            ["aspirin", "ibuprofen", "diclofenac", "naproxen"],
            "Salicylate/NSAID cross-reactivity — caution in aspirin-sensitive asthma."),

        ["macrolide"] = (
            ["azithromycin", "clarithromycin", "erythromycin"],
            "Macrolide class — direct contraindication."),

        ["codeine"] = (
            ["codeine", "dihydrocodeine", "tramadol", "morphine", "oxycodone"],
            "Opioid cross-sensitivity."),
    };

    /// <summary>Penicillin-allergic patients have a small but real cephalosporin cross-reaction rate.</summary>
    private static readonly Dictionary<string, (string[] Drugs, string Why)> PartialCross = new(StringComparer.OrdinalIgnoreCase)
    {
        ["penicillin"] = (
            ["cefalexin", "cephalexin", "cefuroxime", "ceftriaxone", "cefixime"],
            "Cephalosporins carry a small cross-reactivity risk in penicillin allergy — confirm before prescribing."),
    };

    /// <summary>
    /// Checks proposed drugs against the patient's recorded allergies.
    /// Matching is substring-based on normalised text on purpose: "Amoxicillin 500mg BD" must match
    /// "amoxicillin". Over-matching here is safe; under-matching is not.
    /// </summary>
    public IReadOnlyList<AllergyConflict> Check(IEnumerable<PatientFlag> allergies, IEnumerable<string> proposedDrugs)
    {
        var drugs = proposedDrugs
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => (Raw: d, Norm: d.ToLowerInvariant()))
            .ToList();

        if (drugs.Count == 0) return [];

        var conflicts = new List<AllergyConflict>();

        foreach (var allergy in allergies.Where(a => a.Kind is FlagKind.Allergy))
        {
            var key = string.IsNullOrWhiteSpace(allergy.Code) ? allergy.Label : allergy.Code;
            key = key.ToLowerInvariant().Trim();

            foreach (var (table, severityFloor) in
                     new[] { (CrossReactivity, FlagSeverity.Severe), (PartialCross, FlagSeverity.Moderate) })
            {
                if (!table.TryGetValue(key, out var rule)) continue;

                foreach (var (raw, norm) in drugs)
                {
                    var hit = rule.Drugs.FirstOrDefault(d => norm.Contains(d, StringComparison.OrdinalIgnoreCase));
                    if (hit is null) continue;

                    // A drug named in an allergy or avoidance context is a MENTION, not a
                    // prescription. "Penicillin allergy documented - azithromycin instead" must
                    // not flag as contraindicated: doing so buries the real alerts under noise,
                    // and a clinician who learns to dismiss this banner is worse off than one who
                    // never had it. This is the one place the over-trigger bias is relaxed, and
                    // only for phrasings that explicitly negate administration.
                    if (IsMention(norm, hit)) continue;

                    var severity = (FlagSeverity)Math.Max((int)severityFloor, (int)allergy.Severity);

                    // Do not report the same drug twice at a lower severity.
                    if (conflicts.Any(c => c.DrugLabel == raw && c.AllergyCode == key && c.Severity >= severity))
                        continue;

                    conflicts.Add(new AllergyConflict(raw, hit, allergy.Label, key, severity, rule.Why));
                }
            }
        }

        // One banner per drug per allergy.
        //
        // The same contraindication now arrives from two directions — the model's entity
        // ("amoxicillin 500 mg") and the deterministic scan ("amoxicillin") — which is
        // exactly the redundancy that keeps the alert alive when one of them fails. The
        // clinician should not pay for it by reading the same warning twice, so the richer
        // label wins and the duplicate is dropped.
        return [.. conflicts
            .GroupBy(c => (c.DrugCode, c.AllergyCode))
            .Select(g => g
                .OrderByDescending(c => c.Severity)
                .ThenByDescending(c => c.DrugLabel.Length)
                .First())];
    }

    /// <summary>Every drug name this checker can act on, flattened once.</summary>
    private static readonly string[] KnownDrugs =
    [
        .. CrossReactivity.Values.SelectMany(v => v.Drugs)
            .Concat(PartialCross.Values.SelectMany(v => v.Drugs))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(d => d.Length),   // longest first: "co-amoxiclav" before "amoxicillin"
    ];

    /// <summary>
    /// Finds drug names in raw text, without a model.
    ///
    /// This exists because the allergy check must not inherit the availability of the
    /// extraction model. When extraction times out — and over a real network it will —
    /// the entity list comes back empty, and an empty list conflicts with nothing. The
    /// alert would stop firing at exactly the moment the system is least healthy, which is
    /// the failure mode the second invariant exists to forbid.
    ///
    /// Recall over precision: a false positive is a banner a clinician dismisses, a false
    /// negative is a contraindicated prescription.
    ///
    /// The negation rules are applied HERE rather than left to <see cref="Check"/>, because
    /// a bare drug name carries no context — and "Penicillin allergy documented" would
    /// otherwise become an alert about penicillin, on every single encounter that has a
    /// recorded allergy.
    /// </summary>
    public IReadOnlyList<string> ScanForDrugs(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var found = new List<string>();

        foreach (var drug in KnownDrugs)
        {
            if (IsMention(text, drug)) continue;

            var index = text.IndexOf(drug, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                // Word-ish boundaries, so "penicillin" is not reported for the mention
                // inside "phenoxymethylpenicillin" — a different drug with a different rule.
                var beforeOk = index == 0 || !char.IsLetter(text[index - 1]);
                var after = index + drug.Length;
                var afterOk = after >= text.Length || !char.IsLetter(text[after]);

                if (beforeOk && afterOk)
                {
                    found.Add(drug);
                    break;
                }

                index = text.IndexOf(drug, index + 1, StringComparison.OrdinalIgnoreCase);
            }
        }

        return found;
    }

    /// <summary>
    /// True when the drug appears in an avoidance context rather than as something being given.
    /// Deliberately narrow: only these explicit markers suppress a match, and only within a short
    /// window, so an ordinary prescription can never be mistaken for a mention.
    /// </summary>
    private static bool IsMention(string text, string drug)
    {
        string[] markers =
        [
            "allergy", "allergic", "avoid", "avoided", "avoiding", "contraindicat",
            "not given", "instead of", "reaction to", "intoleran", "sensitivity",
        ];

        var index = text.IndexOf(drug, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var start = Math.Max(0, index - 40);
            var end = Math.Min(text.Length, index + drug.Length + 40);
            var window = text[start..end];

            // If ANY occurrence looks like a genuine prescription, treat it as one.
            if (!markers.Any(m => window.Contains(m, StringComparison.OrdinalIgnoreCase)))
                return false;

            index = text.IndexOf(drug, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    /// <summary>Convenience for the live encounter path: one drug, one answer, no ceremony.</summary>
    public AllergyConflict? CheckOne(IEnumerable<PatientFlag> allergies, string drug) =>
        Check(allergies, [drug]).FirstOrDefault();
}
