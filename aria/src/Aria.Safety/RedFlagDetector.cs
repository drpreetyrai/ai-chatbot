using Microsoft.Extensions.Logging;

namespace Aria.Safety;

public sealed record RedFlagVerdict(
    bool IsRedFlag,
    IReadOnlyList<string> Triggers,
    string DetectorVersion,
    string Decision)
{
    public static RedFlagVerdict Clear(string version) => new(false, [], version, "clear");
}

/// <summary>
/// A classifier the detector may consult. Deliberately narrow: it returns a boolean and nothing
/// else. It has no tools, produces no free text, and cannot influence anything but this decision.
/// </summary>
public interface IRedFlagClassifier
{
    Task<bool> IsRedFlagAsync(string text, CancellationToken ct);
}

/// <summary>How long the classifier gets, and how much unreliability is tolerated before it is dropped.</summary>
public sealed record RedFlagClassifierPolicy(
    TimeSpan Budget,
    int ConsecutiveFailuresBeforeTripping,
    TimeSpan CooldownAfterTripping)
{
    /// <summary>
    /// Tuned for a small, fast classification model — the kind plan.md §1.4 routes this task to.
    /// A reasoning model cannot answer inside this budget, which is the point of the breaker below.
    /// </summary>
    public static readonly RedFlagClassifierPolicy Default =
        new(TimeSpan.FromMilliseconds(800), ConsecutiveFailuresBeforeTripping: 3, CooldownAfterTripping: TimeSpan.FromMinutes(5));
}

/// <summary>
/// Invariant 2 (plan.md §1.2): the most safety-critical behaviour in the product does not depend
/// on a probabilistic system.
///
/// The pipeline is: keyword net → optional classifier → UNION, biased to over-trigger.
///
/// Three properties are non-negotiable and each has a test:
///   • If the classifier is absent, slow, or throws, the keyword net alone decides.
///   • A classifier TIMEOUT counts as a POSITIVE, never a negative. We escalate on uncertainty.
///   • This type lives in an assembly that cannot see Aria.Agents, so no prompt, model, or agent
///     bug can reach it.
///
/// A CIRCUIT BREAKER sits around the classifier, and it exists because of a failure seen in
/// practice: point this at a slow (for instance, reasoning) model and every call exceeds the
/// budget, every timeout fails safe, and every routine message escalates. The fail-safe is
/// individually correct and collectively useless — a banner that fires on "should I take my BP
/// tablet before coming?" is a banner the clinic learns to ignore, and then the real one is
/// missed too. So after a few consecutive failures the classifier is dropped for a cooldown and
/// the deterministic net decides alone, which Invariant 2 already guarantees is sufficient.
/// Losing the widener is a quality regression. Alarm fatigue is a safety regression.
/// </summary>
public sealed class RedFlagDetector(
    KeywordNet keywords,
    ILogger<RedFlagDetector> logger,
    IRedFlagClassifier? classifier = null,
    RedFlagClassifierPolicy? policy = null)
{
    public const string Version = "rf-" + KeywordNet.Version;

    private readonly RedFlagClassifierPolicy _policy = policy ?? RedFlagClassifierPolicy.Default;
    private readonly Lock _gate = new();

    private int _consecutiveFailures;
    private DateTimeOffset _trippedUntil = DateTimeOffset.MinValue;

    /// <summary>True while the classifier is being skipped because it proved unreliable.</summary>
    public bool ClassifierTripped
    {
        get { lock (_gate) return DateTimeOffset.UtcNow < _trippedUntil; }
    }

    public async Task<RedFlagVerdict> EvaluateAsync(string text, CancellationToken ct = default)
    {
        // ── Stage 1: deterministic. Always runs, cannot fail, needs no network. ──
        var keywordHits = keywords.Match(text);

        if (keywordHits.Count > 0)
        {
            logger.LogWarning("Red flag raised by keyword net: {Triggers}", string.Join(",", keywordHits));
            return new RedFlagVerdict(true, keywordHits, Version, "keyword_net");
        }

        // ── Stage 2: classifier, strictly as a widener. It can only ever ADD a positive. ──
        if (classifier is null || ClassifierTripped)
            return RedFlagVerdict.Clear(Version);

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(_policy.Budget);

            var classified = await classifier.IsRedFlagAsync(text, budget.Token).ConfigureAwait(false);

            RecordSuccess();

            return classified
                ? new RedFlagVerdict(true, ["classifier_positive"], Version, "classifier")
                : RedFlagVerdict.Clear(Version);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Ran out of time. We do NOT know this message is safe, so we do not say it is.
            RecordFailure("timed out");
            return new RedFlagVerdict(true, ["classifier_timeout"], Version, "timeout_failsafe");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Red-flag classifier failed. Escalating on uncertainty — this is by design.");
            RecordFailure("threw");
            return new RedFlagVerdict(true, ["classifier_error"], Version, "error_failsafe");
        }
    }

    private void RecordSuccess()
    {
        lock (_gate) _consecutiveFailures = 0;
    }

    private void RecordFailure(string what)
    {
        bool justTripped;
        int failures;

        lock (_gate)
        {
            failures = ++_consecutiveFailures;
            justTripped = failures >= _policy.ConsecutiveFailuresBeforeTripping
                       && DateTimeOffset.UtcNow >= _trippedUntil;

            if (justTripped) _trippedUntil = DateTimeOffset.UtcNow.Add(_policy.CooldownAfterTripping);
        }

        if (justTripped)
        {
            logger.LogError(
                "Red-flag classifier {What} {Failures} times in a row and has been DROPPED for {Cooldown}. " +
                "The deterministic keyword net now decides alone, which is sufficient by design. " +
                "Escalating on every message would have been worse than not consulting it at all — " +
                "check that the classification deployment is a fast model, not a reasoning one.",
                what, failures, _policy.CooldownAfterTripping);
        }
        else
        {
            logger.LogWarning(
                "Red-flag classifier {What}. Escalating on uncertainty — this is by design. ({Failures} consecutive)",
                what, failures);
        }
    }
}
