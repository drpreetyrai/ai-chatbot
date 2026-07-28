namespace Aria.Domain.Contracts;

/// <summary>
/// Why a guardrail intervened. These strings appear in the audit log and on the Safety dashboard,
/// so they are part of the product's contract with its operator — not debug text.
/// </summary>
public static class GuardrailReason
{
    public const string PromptInjection      = "prompt_injection";
    public const string IndirectInjection    = "indirect_injection";
    public const string ToolNotRegistered    = "tool_not_registered";
    public const string ToolRoleDenied       = "tool_role_denied";
    public const string ToolAuthorityDenied  = "tool_authority_denied";
    public const string ToolUntrustedOrigin  = "tool_untrusted_origin";
    public const string ToolArgsInvalid      = "tool_args_invalid";
    public const string KillSwitchOff        = "kill_switch_off";
    public const string CitationMissing      = "citation_missing";
    public const string CitationUnresolvable = "citation_unresolvable";
    public const string ProvenanceMissing    = "provenance_missing";
    public const string GroundednessFailed   = "groundedness_failed";
    public const string AllergyConflict      = "allergy_conflict";
    public const string SchemaInvalid        = "schema_invalid";
    public const string ContentModerated     = "content_moderated";
}

/// <summary>Outcome of a shield scan. <c>Documents</c> that fail are removed from context, not merely flagged.</summary>
public sealed record ShieldVerdict(
    bool UserPromptAttackDetected,
    IReadOnlyList<string> AttackedDocumentIds,
    string Detector,
    string? Evidence = null)
{
    public static ShieldVerdict Clean(string detector) => new(false, [], detector);
    public bool AnyAttack => UserPromptAttackDetected || AttackedDocumentIds.Count > 0;
}

/// <summary>A retrieved snippet, carrying the trust level that governs what it may cause.</summary>
public sealed record RetrievedDocument(
    string Id,
    string Title,
    string Text,
    TrustLevel Trust,
    string? Citation = null,
    string? Url = null,
    double Score = 0);

/// <summary>Result of a guardrail-enforced agent run: what came back, and what was stripped on the way.</summary>
public sealed record GuardedResult<T>(
    T? Value,
    bool Allowed,
    IReadOnlyList<string> Interventions,
    string? DenialReason = null)
{
    public static GuardedResult<T> Ok(T value, IReadOnlyList<string>? interventions = null) =>
        new(value, true, interventions ?? []);

    public static GuardedResult<T> Denied(string reason) => new(default, false, [], reason);
}
