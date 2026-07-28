namespace Aria.Domain.Governance;

/// <summary>
/// Resolves the autonomy dial for an intent, most-specific scope first.
///
/// One intent is special: <see cref="RedFlagEscalationIntent"/> is permanently human and this
/// class refuses to return anything else for it — regardless of what is in the database, the
/// configuration, or the request. Some settings should be visibly impossible to change.
/// </summary>
public sealed class AutonomyPolicy(IReadOnlyCollection<AutonomySetting> settings)
{
    public const string RedFlagEscalationIntent = "red_flag_escalation";

    /// <summary>Intents whose mode may never be raised, whatever any caller asks for.</summary>
    public static readonly HashSet<string> ImmutableHumanIntents = [RedFlagEscalationIntent];

    public AutonomyMode Resolve(string intent, string departmentId, string facilityId, string tenantId, DateTimeOffset now)
    {
        if (ImmutableHumanIntents.Contains(intent)) return AutonomyMode.AlwaysHuman;

        foreach (var (kind, id) in new[] { ("department", departmentId), ("facility", facilityId), ("tenant", tenantId) })
        {
            var hit = settings.FirstOrDefault(s =>
                s.Intent == intent && s.ScopeKind == kind && s.ScopeId == id);

            if (hit is null) continue;

            // A promotion that has run out of time silently reverts to Draft. Safe by default.
            if (hit.Mode is AutonomyMode.Auto && hit.ExpiresAt is { } exp && exp <= now)
                return AutonomyMode.Draft;

            return hit.Mode;
        }

        return AutonomyMode.Draft;
    }

    public bool AllowsAutoSend(string intent, string departmentId, string facilityId, string tenantId, DateTimeOffset now) =>
        Resolve(intent, departmentId, facilityId, tenantId, now) is AutonomyMode.Auto;

    /// <summary>
    /// Guards writes to the dials. Demotion toward Draft is always permitted and never gated —
    /// making something safer must never require approval.
    /// </summary>
    public static void GuardChange(string intent, AutonomyMode requested)
    {
        if (ImmutableHumanIntents.Contains(intent))
            throw new AutonomyImmutableException(intent);
    }
}

public sealed class AutonomyImmutableException(string intent)
    : InvalidOperationException($"'{intent}' is permanently human-in-the-loop and cannot be changed.")
{
    public string Intent { get; } = intent;
}
