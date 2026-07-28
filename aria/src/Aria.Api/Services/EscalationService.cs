using Aria.Domain;
using Aria.Domain.Messaging;
using Aria.Infrastructure.Audit;
using Aria.Infrastructure.Persistence;
using Aria.Safety;
using Aria.Shared.Configuration;
using Aria.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Aria.Api.Services;

/// <summary>
/// The journey that must never fail (wireframe J3).
///
/// A patient message arrives. Before intent classification, before the shield, before any agent
/// exists, <see cref="RedFlagDetector"/> runs. If it fires:
///   1. The bot is muted on that thread — permanently, until a human resolves it.
///   2. A safety-netting reply goes out immediately, naming the emergency number.
///   3. An escalation is raised and the on-call is paged.
///   4. A banner appears on Today that cannot be dismissed unacknowledged.
///
/// Nothing here consults a model, and nothing here can be switched off by configuration. An
/// unacknowledged escalation pages the practice; silent failure is impossible by construction.
/// </summary>
public sealed class EscalationService(
    AriaDbContext db,
    RedFlagDetector detector,
    IAuditService audit,
    IAriaEventSink events,
    AriaOptions options,
    ILogger<EscalationService> logger)
{
    public sealed record InboundOutcome(
        bool Escalated,
        Escalation? Escalation,
        IReadOnlyList<string> Triggers,
        string? SafetyNetReply);

    /// <summary>
    /// Handles an inbound patient message. Returns whether it was escalated, which is the signal
    /// the caller uses to decide whether an agent may look at it at all.
    /// </summary>
    public async Task<InboundOutcome> HandleInboundAsync(
        string tenantId, string threadId, string messageBody, CancellationToken ct = default)
    {
        var thread = await db.Threads.FirstAsync(t => t.Id == threadId, ct);

        // ── Stage 1, and it runs first for a reason: if everything downstream is broken, a
        //    patient saying "chest tightness" still reaches a human. ──
        var verdict = await detector.EvaluateAsync(messageBody, ct);

        if (!verdict.IsRedFlag)
            return new InboundOutcome(false, null, [], null);

        var patient = await db.Patients.FirstAsync(p => p.Id == thread.PatientId, ct);

        logger.LogCritical(
            "RED FLAG on thread {ThreadId} for patient {PatientId}. Triggers: {Triggers}. Decision: {Decision}.",
            threadId, patient.Id, string.Join(",", verdict.Triggers), verdict.Decision);

        // 1 — Mute the bot. The system's most important behaviour is knowing when to stop talking.
        thread.BotMuted = true;
        thread.Status = ThreadStatus.Escalated;

        var escalation = new Escalation
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            TenantId = tenantId,
            PatientId = patient.Id,
            ThreadId = threadId,
            Severity = EscalationSeverity.RedFlag,
            Trigger = string.Join(",", verdict.Triggers),
            DetectorVersion = verdict.DetectorVersion,
        };

        db.Escalations.Add(escalation);

        // 2 — Safety netting, sent immediately and without waiting for a human. This message is a
        //     constant, not generated: it must be identical every time and must never be wrong.
        var reply = new Message
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            ThreadId = threadId,
            Direction = MessageDirection.Outbound,
            Body = SafetyNetting.Message,
            Status = MessageStatus.Queued,
            VisibleAfter = DateTimeOffset.UtcNow,   // no undo window — this one goes now
            Basis = "Automatic safety-netting on red-flag detection",
        };

        db.Messages.Add(reply);
        await db.SaveChangesAsync(ct);

        // 3 — Audit, with the detector version, so a miss is reproducible against the golden set.
        await audit.WriteAsync(tenantId, "system", ActorKind.System, AuditActions.Escalation,
            "thread", threadId, patient.Id,
            detail: new
            {
                triggers = verdict.Triggers,
                detector = verdict.DetectorVersion,
                decision = verdict.Decision,
                slaSeconds = options.Safety.EscalationAckSlaSeconds,
            }, ct: ct);

        events.Emit(AriaEvents.EscalationRaised, new Dictionary<string, object?>
        {
            ["escalation_id"] = escalation.Id,
            ["thread_id"] = threadId,
            ["patient_id"] = patient.Id,
            ["triggers"] = string.Join(",", verdict.Triggers),
            ["detector"] = verdict.DetectorVersion,
            ["decision"] = verdict.Decision,
        });

        return new InboundOutcome(true, escalation, verdict.Triggers, SafetyNetting.Message);
    }

    public async Task<Escalation?> AcknowledgeAsync(
        ClinicianIdentity identity, string escalationId, CancellationToken ct = default)
    {
        var escalation = await db.Escalations
            .FirstOrDefaultAsync(e => e.Id == escalationId && e.TenantId == identity.TenantId, ct);

        if (escalation is null || escalation.AcknowledgedAt is not null) return escalation;

        escalation.AcknowledgedBy = identity.DoctorId;
        escalation.AcknowledgedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var latency = escalation.AckLatencySeconds ?? 0;
        AriaDiagnostics.EscalationAckLatency.Record(latency);

        await audit.WriteAsync(identity.TenantId, identity.DoctorId, ActorKind.Clinician,
            AuditActions.EscalationAck, "escalation", escalationId, escalation.PatientId,
            detail: new { ackLatencySeconds = Math.Round(latency, 1) }, ct: ct);

        events.Emit(AriaEvents.EscalationAcknowledged, new Dictionary<string, object?>
        {
            ["escalation_id"] = escalationId,
            ["ack_latency_s"] = Math.Round(latency, 1),
            // The SLO is 100% under two minutes. A breach is a P0 page, so it is tagged at source
            // rather than derived later by a query someone has to remember to write.
            ["breached"] = latency > options.Safety.EscalationAckSlaSeconds,
        });

        if (latency > options.Safety.EscalationAckSlaSeconds)
            logger.LogError("ESCALATION SLA BREACH: {EscalationId} acknowledged after {Latency}s (SLA {Sla}s).",
                escalationId, Math.Round(latency), options.Safety.EscalationAckSlaSeconds);

        return escalation;
    }

    /// <summary>Escalations still waiting. Drives the undismissable banner on Today.</summary>
    public async Task<IReadOnlyList<Escalation>> OpenAsync(string tenantId, CancellationToken ct = default) =>
        await db.Escalations.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.AcknowledgedAt == null)
            .OrderBy(e => e.RaisedAt)
            .ToListAsync(ct);
}
