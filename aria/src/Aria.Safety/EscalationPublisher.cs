using Aria.Domain.Messaging;

namespace Aria.Safety;

/// <summary>
/// Publishes to a topic that is deliberately separate from everything else the system uses.
/// If the agent host is wedged, the outbox is backed up, or the model provider is down, an
/// escalation still goes out.
/// </summary>
public interface IEscalationPublisher
{
    Task PublishAsync(Escalation escalation, CancellationToken ct = default);
}

/// <summary>The safety-net reply sent to the patient the moment the bot stops talking.</summary>
public static class SafetyNetting
{
    public const string Message =
        "Thanks for your message. Because of what you have described, I am getting a person from " +
        "the clinic to look at this right now rather than answering myself.\n\n" +
        "If this feels like an emergency, please do not wait for us — call 108 (or your local " +
        "emergency number) or go to your nearest emergency department now.";

    /// <summary>
    /// The message the clinician sees. Names the trigger and the detector so the on-call can
    /// judge in seconds whether it is real, without opening a dashboard.
    /// </summary>
    public static string ClinicianAlert(string patientName, IReadOnlyList<string> triggers, string quote) =>
        $"RED FLAG · {patientName} · triggers: {string.Join(", ", triggers)}\n\"{Truncate(quote, 140)}\"";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max - 1), "…");
}
