namespace Aria.Domain.Encounters;

/// <summary>
/// The encounter lifecycle, as an explicit machine rather than scattered if-statements.
/// Two rules are load-bearing:
///   1. Recording cannot start without granted consent.
///   2. Nothing reaches <see cref="EncounterState.Signed"/> except through review.
/// </summary>
public static class EncounterStateMachine
{
    private static readonly Dictionary<EncounterState, EncounterState[]> Allowed = new()
    {
        [EncounterState.Scheduled]         = [EncounterState.CheckedIn, EncounterState.Abandoned],
        [EncounterState.CheckedIn]         = [EncounterState.Recording, EncounterState.Abandoned],
        [EncounterState.Recording]         = [EncounterState.Paused, EncounterState.Ended],
        [EncounterState.Paused]            = [EncounterState.Recording, EncounterState.Ended],
        [EncounterState.Ended]             = [EncounterState.Drafting],
        [EncounterState.Drafting]          = [EncounterState.AwaitingSignature, EncounterState.Ended],
        [EncounterState.AwaitingSignature] = [EncounterState.Signed, EncounterState.Abandoned],
        [EncounterState.Signed]            = [],
        [EncounterState.Abandoned]         = [],
    };

    public static bool CanTransition(EncounterState from, EncounterState to) =>
        Allowed.TryGetValue(from, out var next) && next.Contains(to);

    public static void Transition(Encounter encounter, EncounterState to, bool consentGranted)
    {
        if (to is EncounterState.Recording && !consentGranted)
            throw new InvalidOperationException(
                "Capture cannot start without granted consent. The clinician may still document manually.");

        if (!CanTransition(encounter.State, to))
            throw new InvalidOperationException($"Illegal encounter transition {encounter.State} -> {to}.");

        encounter.State = to;

        if (to is EncounterState.Recording && encounter.StartedAt is null)
            encounter.StartedAt = DateTimeOffset.UtcNow;
        if (to is EncounterState.Ended)
            encounter.EndedAt = DateTimeOffset.UtcNow;
    }
}
