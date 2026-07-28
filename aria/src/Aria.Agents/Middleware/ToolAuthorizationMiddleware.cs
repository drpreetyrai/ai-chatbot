using Aria.Agents.Runtime;
using Aria.Agents.Tools;
using Aria.Domain;
using Aria.Domain.Contracts;
using Aria.Shared.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aria.Agents.Middleware;

/// <summary>
/// The six-question gate every tool call passes through (plan.md §4.3).
///
/// Two of the six are different in kind from the rest. Questions 3 and 5 — an unauthorised
/// authority for the lifecycle stage, and a call whose origin is untrusted content — are not
/// retryable errors handed back to the model to work around. They terminate the turn and raise
/// an audit event, because an agent attempting an unauthorised commit is a security signal.
/// </summary>
public sealed class ToolAuthorizationMiddleware(
    IAriaEventSink events,
    ILogger<ToolAuthorizationMiddleware> logger,
    IFeatureSwitches features)
{
    public async ValueTask<object?> InvokeAsync(
        Microsoft.Agents.AI.AIAgent agent,
        FunctionInvocationContext ctx,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken ct)
    {
        var scope = GuardrailScope.Current;
        var toolName = ctx.Function.Name;
        var descriptor = ToolCatalog.Find(toolName);

        using var activity = AriaDiagnostics.StartTool(toolName, descriptor?.Authority.ToString() ?? "unknown");

        // 1 — Is the tool known and registered at all? Unknown means denied, never default-allow.
        if (descriptor is null)
            return Deny(scope, toolName, GuardrailReason.ToolNotRegistered,
                $"Tool '{toolName}' is not in the catalogue and cannot be called.", terminal: true);

        // 2 — Does the caller's role permit it?
        var role = scope?.Context.Identity.Role;
        if (role is null || !descriptor.AllowedRoles.Contains(role.Value))
            return Deny(scope, toolName, GuardrailReason.ToolRoleDenied,
                $"Role '{role}' is not permitted to call '{toolName}'.", terminal: true);

        // 3 — Does the authority match the lifecycle stage? Commit before signature is terminal.
        var lifecycle = scope!.Context.Lifecycle;
        if (!ToolCatalog.IsLegalAt(descriptor.Authority, lifecycle))
        {
            logger.LogError(
                "SECURITY: agent {Agent} attempted {Authority} tool '{Tool}' at lifecycle {Lifecycle}.",
                scope.AgentId, descriptor.Authority, toolName, lifecycle);

            return Deny(scope, toolName, GuardrailReason.ToolAuthorityDenied,
                $"'{toolName}' requires {descriptor.Authority} authority, which is not available before signature.",
                terminal: true);
        }

        // 4 — Is the capability switched on for this tenant/department?
        if (!features.IsToolEnabled(toolName, scope.Context))
            return Deny(scope, toolName, GuardrailReason.KillSwitchOff,
                $"'{toolName}' is currently disabled for {scope.Context.Department}. Use the manual path.",
                terminal: false);

        // 5 — Did this call originate from untrusted content?
        //     Reads stay available: a patient's question SHOULD be answerable. Anything that
        //     drafts, holds or commits does not.
        if (scope.HasUntrustedContent && descriptor.Authority != ToolAuthority.Read)
        {
            logger.LogError(
                "SECURITY: {Authority} tool '{Tool}' requested while untrusted content is in context. Refused.",
                descriptor.Authority, toolName);

            return Deny(scope, toolName, GuardrailReason.ToolUntrustedOrigin,
                $"'{toolName}' cannot be called while untrusted content is in context. " +
                "A human must take this action.", terminal: true);
        }

        // 6 — Execute, and record what happened.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var result = await next(ctx, ct).ConfigureAwait(false);
            var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            activity?.SetTag(AriaTags.Outcome, "ok").SetTag(AriaTags.LatencyMs, ms);
            events.Emit("tool.invoked", new Dictionary<string, object?>
            {
                ["tool"] = toolName,
                ["authority"] = descriptor.Authority.ToString(),
                ["latency_ms"] = Math.Round(ms, 1),
            });

            return result;
        }
        catch (Exception ex)
        {
            activity?.SetTag(AriaTags.Outcome, "error");
            logger.LogError(ex, "Tool '{Tool}' threw.", toolName);

            // A typed error the model can reason about — not a wall it hits and gives up against.
            return $"TOOL_ERROR: {toolName} failed ({ex.GetType().Name}). " +
                   "Do not retry more than once; prefer telling the clinician you could not complete this.";
        }
    }

    private object Deny(GuardrailScope? scope, string tool, string reason, string message, bool terminal)
    {
        scope?.Intervene(reason, tool);

        events.Emit(AriaEvents.GuardrailPrefix + reason, new Dictionary<string, object?>
        {
            ["tool"] = tool,
            ["agent"] = scope?.AgentId,
            ["terminal"] = terminal,
        });

        logger.LogWarning("Tool '{Tool}' denied: {Reason}", tool, reason);

        return terminal
            ? throw new ToolAuthorizationException(reason, message, tool)
            : $"TOOL_DENIED: {message}";
    }
}

/// <summary>Terminates the run. Caught at the agent boundary and turned into a degraded response.</summary>
public sealed class ToolAuthorizationException(string reason, string message, string tool)
    : InvalidOperationException(message)
{
    public string Reason { get; } = reason;
    public string Tool { get; } = tool;
}

/// <summary>
/// Kill switches, scoped tenant → facility → department. Backed by Azure App Configuration in
/// production; an in-memory implementation locally so the chaos test can flip them.
/// </summary>
public interface IFeatureSwitches
{
    bool IsToolEnabled(string toolName, AgentContext context);
    bool IsAgentEnabled(string agentId, AgentContext context);
    void Set(string key, bool enabled);
    IReadOnlyDictionary<string, bool> Snapshot();
}
