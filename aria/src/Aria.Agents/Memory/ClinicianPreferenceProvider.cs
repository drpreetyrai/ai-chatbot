using System.Text;
using Aria.Agents.Runtime;
using Microsoft.Agents.AI;

namespace Aria.Agents.Memory;

public sealed record ClinicianPreference(string Kind, string Value, string LearnedFrom, DateTimeOffset UpdatedAt);

/// <summary>
/// Long-term PROCEDURAL memory: how this particular clinician likes their notes written.
///
/// Learned from the diff between draft and signed note, never from a settings page nobody fills
/// in. Fully inspectable and deletable by the clinician at Settings → "What Aria has learned
/// about my style". Preferences never cross clinicians and never cross tenants.
/// </summary>
public interface IClinicianPreferenceStore
{
    Task<IReadOnlyList<ClinicianPreference>> GetAsync(string doctorId, CancellationToken ct = default);
    Task LearnAsync(string doctorId, string kind, string value, string learnedFrom, CancellationToken ct = default);
    Task ForgetAsync(string doctorId, string kind, CancellationToken ct = default);
}

public sealed class ClinicianPreferenceProvider(
    IClinicianPreferenceStore store, AgentContext context) : AIContextProvider
{
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext ctx, CancellationToken cancellationToken)
    {
        var prefs = await store.GetAsync(context.DoctorId, cancellationToken);
        if (prefs.Count == 0) return new AIContext();

        var sb = new StringBuilder();
        sb.AppendLine("<clinician_style>");
        sb.AppendLine("Preferences learned from this clinician's own accepted edits. Match them where");
        sb.AppendLine("it does not compromise clinical accuracy. Accuracy always wins over style.");
        foreach (var p in prefs) sb.AppendLine($"  {p.Kind}: {p.Value}");
        sb.AppendLine("</clinician_style>");

        return new AIContext { Instructions = sb.ToString() };
    }
}

/// <summary>
/// Dependency-free store so procedural memory works out of the box. Swapped for the
/// Postgres-backed implementation in production.
/// </summary>
public sealed class InMemoryPreferenceStore : IClinicianPreferenceStore
{
    private readonly Dictionary<string, Dictionary<string, ClinicianPreference>> _byDoctor = [];
    private readonly Lock _gate = new();

    public Task<IReadOnlyList<ClinicianPreference>> GetAsync(string doctorId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<ClinicianPreference> result = _byDoctor.TryGetValue(doctorId, out var m)
                ? [.. m.Values.OrderBy(p => p.Kind)]
                : [];
            return Task.FromResult(result);
        }
    }

    public Task LearnAsync(string doctorId, string kind, string value, string learnedFrom, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_byDoctor.TryGetValue(doctorId, out var m)) _byDoctor[doctorId] = m = [];
            m[kind] = new ClinicianPreference(kind, value, learnedFrom, DateTimeOffset.UtcNow);
        }
        return Task.CompletedTask;
    }

    public Task ForgetAsync(string doctorId, string kind, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_byDoctor.TryGetValue(doctorId, out var m)) m.Remove(kind);
        }
        return Task.CompletedTask;
    }
}
