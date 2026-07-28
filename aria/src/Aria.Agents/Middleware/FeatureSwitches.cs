using System.Collections.Concurrent;
using Aria.Agents.Runtime;

namespace Aria.Agents.Middleware;

/// <summary>
/// Default-on for tools, but every AI feature can be switched off in under a second, and
/// switching one off degrades to the manual path rather than showing an error.
/// </summary>
public sealed class InMemoryFeatureSwitches : IFeatureSwitches
{
    private readonly ConcurrentDictionary<string, bool> _switches = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryFeatureSwitches()
    {
        foreach (var agent in new[]
                 {
                     AgentIds.Scribe, AgentIds.Extraction, AgentIds.ChartQa,
                     AgentIds.ClinicalEvidence, AgentIds.Scheduling, AgentIds.PatientComms,
                 })
            _switches[$"agent.{agent}"] = true;
    }

    public bool IsToolEnabled(string toolName, AgentContext context) =>
        Lookup($"tool.{toolName}", context) ?? true;

    public bool IsAgentEnabled(string agentId, AgentContext context) =>
        Lookup($"agent.{agentId}", context) ?? true;

    /// <summary>Most specific scope wins: department overrides facility overrides tenant overrides global.</summary>
    private bool? Lookup(string key, AgentContext context)
    {
        foreach (var scoped in new[]
                 {
                     $"{key}@department:{context.Department}",
                     $"{key}@facility:{context.FacilityId}",
                     $"{key}@tenant:{context.TenantId}",
                     key,
                 })
            if (_switches.TryGetValue(scoped, out var value)) return value;

        return null;
    }

    public void Set(string key, bool enabled) => _switches[key] = enabled;

    public IReadOnlyDictionary<string, bool> Snapshot() => _switches.ToDictionary(k => k.Key, v => v.Value);
}
