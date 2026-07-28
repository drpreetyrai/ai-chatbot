namespace Aria.Shared.Telemetry;

/// <summary>
/// Product events go here as well as to OpenTelemetry. Keeping an in-process sink means the
/// Insights and Safety dashboards work with zero external configuration — you can see the
/// instrumentation working on day one instead of after an App Insights ingestion delay.
/// </summary>
public interface IAriaEventSink
{
    void Emit(string eventName, IReadOnlyDictionary<string, object?>? tags = null);
    IReadOnlyList<AriaEventRecord> Recent(int take = 200, string? prefix = null);
    IReadOnlyDictionary<string, long> Counts();
}

public sealed record AriaEventRecord(
    DateTimeOffset At,
    string Name,
    IReadOnlyDictionary<string, object?> Tags);
