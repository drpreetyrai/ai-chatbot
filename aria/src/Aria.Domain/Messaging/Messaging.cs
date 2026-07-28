namespace Aria.Domain.Messaging;

public sealed class MessageThread
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string PatientId { get; init; }
    public string Channel { get; init; } = "whatsapp";
    public ThreadStatus Status { get; set; } = ThreadStatus.Open;
    public string? AssignedTo { get; set; }

    /// <summary>
    /// WhatsApp's 24-hour service window. A platform constraint that changes behaviour belongs
    /// in the UI, not in a developer's head (wireframe S-07) — so we model it explicitly.
    /// </summary>
    public DateTimeOffset? ServiceWindowExpiresAt { get; set; }

    /// <summary>Red-flag threads mute the bot. Knowing when to stop talking is the point.</summary>
    public bool BotMuted { get; set; }

    public TimeSpan? WindowRemaining(DateTimeOffset now) =>
        ServiceWindowExpiresAt is { } e && e > now ? e - now : null;

    public bool RequiresTemplate(DateTimeOffset now) => WindowRemaining(now) is null;
}

public sealed class Message
{
    public required string Id { get; init; }
    public required string ThreadId { get; init; }
    public required MessageDirection Direction { get; init; }
    public required string Body { get; init; }
    public string? TemplateId { get; init; }
    public MessageStatus Status { get; set; } = MessageStatus.Draft;
    public string? ExternalRef { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>
    /// Reversibility as a schedule, not a recall. The message becomes visible to the dispatcher
    /// only after the undo window elapses; undo simply deletes the row (plan.md §3.5).
    /// </summary>
    public DateTimeOffset? VisibleAfter { get; set; }

    public double? Confidence { get; init; }
    /// <summary>"Basis: active med list · pre-visit policy v3" — shown on the draft (wireframe S-07).</summary>
    public string? Basis { get; init; }
    public TrustLevel Trust { get; init; } = TrustLevel.Trusted;

    public bool CanUndo(DateTimeOffset now) =>
        Status is MessageStatus.Queued && VisibleAfter is { } v && v > now;
}

/// <summary>
/// Patient-facing generation is template-bounded by construction. Free-form text to a patient is
/// architecturally impossible, which bounds the blast radius of any prompt injection (plan.md §7, D5).
/// </summary>
public sealed class MessageTemplate
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string Intent { get; init; }
    public required string Language { get; init; }
    /// <summary>Body with {{placeholders}}. The model may only fill placeholders, never the prose.</summary>
    public required string Body { get; init; }
    public required string[] Parameters { get; init; }
    public bool Active { get; init; } = true;

    public string Render(IReadOnlyDictionary<string, string> values)
    {
        var missing = Parameters.Where(p => !values.ContainsKey(p)).ToArray();
        if (missing.Length > 0)
            throw new ArgumentException($"Template '{Id}' is missing parameter(s): {string.Join(", ", missing)}");

        var body = Body;
        foreach (var p in Parameters) body = body.Replace("{{" + p + "}}", values[p]);
        return body;
    }
}

/// <summary>
/// The journey that must never fail. An unacknowledged escalation pages the practice;
/// silent failure is impossible by construction (wireframe J3).
/// </summary>
public sealed class Escalation
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string PatientId { get; init; }
    public string? ThreadId { get; init; }
    public required EscalationSeverity Severity { get; init; }
    public required string Trigger { get; init; }
    /// <summary>Which detector version fired — so a miss is reproducible against the golden set.</summary>
    public required string DetectorVersion { get; init; }
    public DateTimeOffset RaisedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? AcknowledgedBy { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    public bool IsBreached(DateTimeOffset now, int slaSeconds) =>
        AcknowledgedAt is null && (now - RaisedAt).TotalSeconds > slaSeconds;

    public double? AckLatencySeconds =>
        AcknowledgedAt is { } a ? (a - RaisedAt).TotalSeconds : null;
}
