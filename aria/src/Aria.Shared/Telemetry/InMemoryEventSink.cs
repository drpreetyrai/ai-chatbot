using System.Collections.Concurrent;
using System.Diagnostics;

namespace Aria.Shared.Telemetry;

/// <summary>
/// Bounded ring buffer. Deliberately capped: telemetry must never be the thing that exhausts
/// memory on a clinic-day traffic spike.
/// </summary>
public sealed class InMemoryEventSink(int capacity = 5_000) : IAriaEventSink
{
    private readonly ConcurrentQueue<AriaEventRecord> _events = new();
    private readonly ConcurrentDictionary<string, long> _counts = new();

    public void Emit(string eventName, IReadOnlyDictionary<string, object?>? tags = null)
    {
        var enriched = new Dictionary<string, object?>(tags ?? new Dictionary<string, object?>());

        // Fold ambient Activity baggage in, so callers never have to pass it explicitly.
        if (Activity.Current is { } activity)
        {
            foreach (var (k, v) in activity.Baggage)
                enriched.TryAdd(k, v);
            enriched.TryAdd("trace_id", activity.TraceId.ToString());
        }

        _events.Enqueue(new AriaEventRecord(DateTimeOffset.UtcNow, eventName, enriched));
        _counts.AddOrUpdate(eventName, 1, (_, c) => c + 1);

        while (_events.Count > capacity) _events.TryDequeue(out _);

        AriaDiagnostics.Events.Add(1, new KeyValuePair<string, object?>("event", eventName));
        if (eventName.StartsWith(AriaEvents.GuardrailPrefix, StringComparison.Ordinal))
            AriaDiagnostics.GuardrailInterventions.Add(1, new KeyValuePair<string, object?>("reason", eventName));
    }

    public IReadOnlyList<AriaEventRecord> Recent(int take = 200, string? prefix = null) =>
        _events.Reverse()
               .Where(e => prefix is null || e.Name.StartsWith(prefix, StringComparison.Ordinal))
               .Take(take)
               .ToList();

    public IReadOnlyDictionary<string, long> Counts() => _counts.ToDictionary(k => k.Key, v => v.Value);
}
