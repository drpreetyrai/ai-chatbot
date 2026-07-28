using Aria.Domain;
using Aria.Domain.Patients;
using Aria.Safety;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aria.Tests;

/// <summary>
/// The red-flag detector's three non-negotiable properties, plus recall against the golden set.
///
/// Target recall is 100% and the CI gate has no override (plan.md §9.3). A regression here is not
/// a failing test — it is a product that must not ship.
/// </summary>
public class RedFlagDetectorTests
{
    private static RedFlagDetector Detector(IRedFlagClassifier? classifier = null) =>
        new(new KeywordNet(), NullLogger<RedFlagDetector>.Instance, classifier);

    /// <summary>
    /// The golden set: the phrasings people actually use — colloquial, misspelled, code-switched,
    /// and evasive — not the textbook terms a developer would think of first.
    /// </summary>
    public static TheoryData<string> MustEscalate =>
    [
        "chest tightness since morning",
        "I have chest pain",
        "my chest feels tight and heavy",
        "CHEST PAIN!!!",
        "chesssst paaaain",                      // repeated letters
        "ch3st p4in",                            // leetspeak evasion
        "seene mein dard ho raha hai",           // Hindi, transliterated
        "saans nahi aa rahi",                    // Hindi: cannot breathe
        "i cant breathe properly",
        "having trouble breathing since last night",
        "short of breath climbing stairs",
        "worst headache of my life",
        "sudden weakness on my left side",
        "my face is drooping and speech is slurred",
        "coughing up blood this morning",
        "blood in my stool",
        "bleeding heavily and it wont stop",
        "severe abdominal pain",
        "im 30 weeks pregnant and bleeding",
        "the baby is not moving",
        "my throat is swelling up",
        "i think im having an allergic reaction",
        "i want to kill myself",
        "thinking about ending my life",
        "i took all my tablets at once",
        "please send an ambulance",
        "i think im dying",
        "lips turning blue",
        "passed out at work",
        "had a seizure this morning",
    ];

    [Theory]
    [MemberData(nameof(MustEscalate))]
    public async Task Golden_set_recall_is_100_percent(string message)
    {
        var verdict = await Detector().EvaluateAsync(message);

        Assert.True(verdict.IsRedFlag,
            $"MISSED RED FLAG: \"{message}\". Recall must be 100% — this blocks release.");
    }

    [Theory]
    [InlineData("Should I take my BP tablet before coming?")]
    [InlineData("Can I eat before the blood test?")]
    [InlineData("Can I reschedule to next week?")]
    [InlineData("Thank you doctor, feeling much better")]
    [InlineData("What time is my appointment tomorrow?")]
    public async Task Routine_messages_are_not_escalated(string message)
    {
        var verdict = await Detector().EvaluateAsync(message);

        // Precision matters too: alarm fatigue is a real failure mode. But note the asymmetry —
        // a miss here is a nuisance, a miss above is an incident.
        Assert.False(verdict.IsRedFlag, $"False escalation on: \"{message}\"");
    }

    [Fact]
    public async Task Keyword_net_alone_is_sufficient_when_no_classifier_exists()
    {
        var verdict = await Detector(classifier: null).EvaluateAsync("chest tightness since morning");

        Assert.True(verdict.IsRedFlag);
        Assert.Equal("keyword_net", verdict.Decision);
    }

    [Fact]
    public async Task Classifier_timeout_counts_as_a_positive()
    {
        // The property that makes this safe: uncertainty escalates. A classifier that hangs must
        // never be read as "probably fine".
        var verdict = await Detector(new HangingClassifier())
            .EvaluateAsync("something is wrong but I cannot describe it");

        Assert.True(verdict.IsRedFlag);
        Assert.Equal("timeout_failsafe", verdict.Decision);
    }

    [Fact]
    public async Task Classifier_exception_counts_as_a_positive()
    {
        var verdict = await Detector(new ThrowingClassifier())
            .EvaluateAsync("something is wrong but I cannot describe it");

        Assert.True(verdict.IsRedFlag);
        Assert.Equal("error_failsafe", verdict.Decision);
    }

    [Fact]
    public async Task Classifier_can_only_widen_never_narrow()
    {
        // A classifier answering "routine" must not be able to overrule the deterministic net.
        var verdict = await Detector(new AlwaysRoutineClassifier())
            .EvaluateAsync("chest tightness since morning");

        Assert.True(verdict.IsRedFlag);
        Assert.Equal("keyword_net", verdict.Decision);
    }

    private sealed class HangingClassifier : IRedFlagClassifier
    {
        public async Task<bool> IsRedFlagAsync(string text, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return false;
        }
    }

    private sealed class ThrowingClassifier : IRedFlagClassifier
    {
        public Task<bool> IsRedFlagAsync(string text, CancellationToken ct) =>
            throw new HttpRequestException("model provider unavailable");
    }

    private sealed class AlwaysRoutineClassifier : IRedFlagClassifier
    {
        public Task<bool> IsRedFlagAsync(string text, CancellationToken ct) => Task.FromResult(false);
    }
}

/// <summary>
/// Contraindication detection. Also a 100% recall gate — a missed allergy conflict is the failure
/// mode that ends the product, and it is entirely preventable with rules.
/// </summary>
public class AllergyConflictCheckerTests
{
    private static readonly AllergyConflictChecker Checker = new();

    private static PatientFlag Allergy(string code, FlagSeverity severity = FlagSeverity.Severe) => new()
    {
        Id = "f1", PatientId = "p1", Kind = FlagKind.Allergy,
        Code = code, Label = $"{code} allergy", Severity = severity,
        RecordedAt = DateTimeOffset.UtcNow,
    };

    [Theory]
    [InlineData("penicillin", "amoxicillin 500mg BD")]
    [InlineData("penicillin", "Amoxicillin")]
    [InlineData("penicillin", "co-amoxiclav 625mg")]
    [InlineData("penicillin", "flucloxacillin")]
    [InlineData("penicillin", "3. Augmentin 625 mg TDS x 5 days")]
    [InlineData("sulfonamide", "co-trimoxazole")]
    [InlineData("macrolide", "azithromycin 500mg OD")]
    [InlineData("nsaid", "ibuprofen 400mg")]
    [InlineData("nsaid", "diclofenac gel")]
    [InlineData("codeine", "tramadol 50mg")]
    public void Conflicts_are_detected(string allergyCode, string proposedDrug)
    {
        var conflicts = Checker.Check([Allergy(allergyCode)], [proposedDrug]);

        Assert.NotEmpty(conflicts);
    }

    [Theory]
    [InlineData("penicillin", "azithromycin 500mg OD")]
    [InlineData("penicillin", "paracetamol 500mg")]
    [InlineData("nsaid", "paracetamol 500mg")]
    [InlineData("macrolide", "amoxicillin 500mg")]
    public void Safe_alternatives_are_not_flagged(string allergyCode, string proposedDrug)
    {
        var conflicts = Checker.Check([Allergy(allergyCode)], [proposedDrug]);

        Assert.Empty(conflicts);
    }

    [Fact]
    public void Penicillin_allergy_flags_cephalosporins_as_partial_cross_reactivity()
    {
        // Small but real cross-reaction rate. "Probably fine" is not a clinical standard, so it
        // surfaces as a moderate conflict for the clinician to judge rather than silence.
        var conflicts = Checker.Check([Allergy("penicillin")], ["cefalexin 500mg"]);

        Assert.NotEmpty(conflicts);
        Assert.Contains("cross-reactivity", conflicts[0].Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_demo_scenario_is_caught()
    {
        // The exact case from wireframe S-03/S-04: the doctor reaches for amoxicillin aloud and
        // the patient is penicillin-allergic. If this ever stops firing, the demo is a liability.
        var conflicts = Checker.Check(
            [Allergy("penicillin")],
            ["Paracetamol 500 mg BD", "Amoxicillin 500 mg TDS", "Chest X-ray PA"]);

        var conflict = Assert.Single(conflicts);
        Assert.Equal("amoxicillin", conflict.DrugCode);
        Assert.Equal(FlagSeverity.Severe, conflict.Severity);
    }

    [Fact]
    public void No_allergies_means_no_conflicts()
    {
        Assert.Empty(Checker.Check([], ["amoxicillin 500mg"]));
    }

    [Fact]
    public void Empty_drug_list_is_handled()
    {
        Assert.Empty(Checker.Check([Allergy("penicillin")], []));
    }
}

/// <summary>
/// The circuit breaker around the red-flag classifier.
///
/// These exist because of a failure found by running the product against a real
/// model plane: a classifier slower than its budget times out on every call, each
/// timeout correctly fails safe, and the net effect is that every routine message
/// escalates. Individually correct, collectively a safety regression — a banner
/// that fires constantly is a banner the clinic stops reading.
/// </summary>
public class RedFlagClassifierBreakerTests
{
    private static readonly RedFlagClassifierPolicy Fast =
        new(TimeSpan.FromMilliseconds(50), ConsecutiveFailuresBeforeTripping: 3,
            CooldownAfterTripping: TimeSpan.FromMinutes(5));

    private sealed class AlwaysSlow : IRedFlagClassifier
    {
        public int Calls;
        public async Task<bool> IsRedFlagAsync(string text, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return false;
        }
    }

    private sealed class Recovering(int failFirst) : IRedFlagClassifier
    {
        private int _seen;
        public async Task<bool> IsRedFlagAsync(string text, CancellationToken ct)
        {
            if (++_seen <= failFirst) { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            return false;
        }
    }

    [Fact]
    public async Task A_persistently_slow_classifier_is_dropped_rather_than_escalating_everything()
    {
        var slow = new AlwaysSlow();
        var detector = new RedFlagDetector(new KeywordNet(), NullLogger<RedFlagDetector>.Instance, slow, Fast);

        // The first few failures still fail safe — a one-off timeout must escalate.
        for (var i = 0; i < 3; i++)
            Assert.True((await detector.EvaluateAsync("Can I eat before the blood test?")).IsRedFlag);

        Assert.True(detector.ClassifierTripped);

        // Once tripped, routine messages stop escalating and the keyword net decides alone.
        var verdict = await detector.EvaluateAsync("Can I eat before the blood test?");
        Assert.False(verdict.IsRedFlag);
        Assert.Equal("clear", verdict.Decision);

        var callsBefore = slow.Calls;
        await detector.EvaluateAsync("What time is my appointment?");
        Assert.Equal(callsBefore, slow.Calls);   // not even consulted
    }

    [Fact]
    public async Task Dropping_the_classifier_never_weakens_the_deterministic_net()
    {
        // The property that makes the breaker safe to have at all: the keyword net is
        // sufficient on its own, so losing the widener costs quality, never recall.
        var detector = new RedFlagDetector(
            new KeywordNet(), NullLogger<RedFlagDetector>.Instance, new AlwaysSlow(), Fast);

        for (var i = 0; i < 3; i++) await detector.EvaluateAsync("routine question");
        Assert.True(detector.ClassifierTripped);

        foreach (var emergency in new[]
                 {
                     "chest tightness since morning",
                     "I can't breathe",
                     "I want to kill myself",
                     "coughing up blood",
                 })
        {
            var verdict = await detector.EvaluateAsync(emergency);
            Assert.True(verdict.IsRedFlag, $"MISSED after the breaker tripped: \"{emergency}\"");
            Assert.Equal("keyword_net", verdict.Decision);
        }
    }

    [Fact]
    public async Task An_intermittent_failure_does_not_trip_the_breaker()
    {
        // One slow call is a blip, not a broken deployment. The fail-safe should stay armed.
        var detector = new RedFlagDetector(
            new KeywordNet(), NullLogger<RedFlagDetector>.Instance, new Recovering(failFirst: 1), Fast);

        Assert.True((await detector.EvaluateAsync("routine")).IsRedFlag);    // failed safe
        Assert.False((await detector.EvaluateAsync("routine")).IsRedFlag);   // recovered
        Assert.False(detector.ClassifierTripped);

        Assert.True((await detector.EvaluateAsync("routine")).IsRedFlag is false);
    }
}

/// <summary>
/// The model-free drug scan.
///
/// This is the path that keeps the contraindication alert alive when the extraction
/// model is slow, rate-limited or down. It was added after exactly that happened against
/// a live model: extraction overran its budget, returned nothing, and the alert stopped
/// firing with no visible symptom at all.
/// </summary>
public class DrugScanTests
{
    private static readonly AllergyConflictChecker Checker = new();

    private static PatientFlag Allergy(string code) => new()
    {
        Id = "f1", PatientId = "p1", Kind = FlagKind.Allergy,
        Code = code, Label = $"{code} allergy", Severity = FlagSeverity.Severe,
        RecordedAt = DateTimeOffset.UtcNow,
    };

    [Theory]
    [InlineData("Let's start you on amoxicillin 500 milligrams twice a day.", "amoxicillin")]
    [InlineData("I'll write you co-amoxiclav for five days.", "co-amoxiclav")]
    [InlineData("Take ibuprofen 400mg if the pain returns.", "ibuprofen")]
    [InlineData("We'll give azithromycin instead.", "azithromycin")]
    public void A_drug_is_found_in_ordinary_speech(string transcript, string expected)
    {
        Assert.Contains(expected, Checker.ScanForDrugs(transcript), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_longer_drug_name_is_not_reported_as_the_shorter_one_inside_it()
    {
        var found = Checker.ScanForDrugs("Phenoxymethylpenicillin 250mg qds.");

        Assert.Contains("phenoxymethylpenicillin", found, StringComparer.OrdinalIgnoreCase);

        // Reporting a bare "penicillin" here would name a drug that was never prescribed,
        // and the clinician would be checking the wrong thing.
        Assert.DoesNotContain("penicillin", found, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Nothing_is_found_in_a_transcript_with_no_drugs()
    {
        Assert.Empty(Checker.ScanForDrugs("Fever for three days, no vomiting, appetite is fine."));
    }

    [Fact]
    public void The_conflict_still_fires_when_the_model_extracted_nothing()
    {
        const string transcript =
            "Doctor: Fever for three days. I'm going to start you on amoxicillin 500 milligrams.";

        // This is the degraded path exactly: no model entities, only the raw window.
        var conflicts = Checker.Check([Allergy("penicillin")], Checker.ScanForDrugs(transcript));

        var conflict = Assert.Single(conflicts);
        Assert.Equal("amoxicillin", conflict.DrugCode);
        Assert.Equal(FlagSeverity.Severe, conflict.Severity);
    }

    [Fact]
    public void The_scan_does_not_turn_a_documented_allergy_into_an_alert()
    {
        const string transcript =
            "Penicillin allergy documented — rash as a child. Prescribing azithromycin instead.";

        var conflicts = Checker.Check([Allergy("penicillin")], Checker.ScanForDrugs(transcript));

        // The scan is deliberately greedy; Check is what applies the negation rules. A
        // banner on every recorded allergy would teach clinicians to dismiss the banner.
        Assert.Empty(conflicts);
    }
}

public class ConflictDeduplicationTests
{
    private static readonly AllergyConflictChecker Checker = new();

    private static PatientFlag Penicillin() => new()
    {
        Id = "f1", PatientId = "p1", Kind = FlagKind.Allergy,
        Code = "penicillin", Label = "Penicillin allergy", Severity = FlagSeverity.Severe,
        RecordedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void The_same_drug_from_two_sources_produces_one_warning()
    {
        // What actually happens live: the model returns the dosed entity, the deterministic
        // scan returns the bare name. Both are correct; two identical banners are not.
        var conflicts = Checker.Check([Penicillin()], ["amoxicillin 500 mg", "amoxicillin"]);

        var conflict = Assert.Single(conflicts);

        // And the more specific label is the one the clinician sees.
        Assert.Equal("amoxicillin 500 mg", conflict.DrugLabel);
    }

    [Fact]
    public void Two_different_contraindicated_drugs_still_produce_two_warnings()
    {
        var conflicts = Checker.Check([Penicillin()], ["amoxicillin 500mg", "flucloxacillin 250mg"]);

        Assert.Equal(2, conflicts.Count);
    }
}
