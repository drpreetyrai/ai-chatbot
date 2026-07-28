using Aria.Agents.Agents;
using Aria.Agents.Runtime;
using Aria.Domain;
using Aria.Domain.Governance;
using Aria.Domain.Messaging;
using Aria.Infrastructure.Audit;
using Aria.Infrastructure.Persistence;
using Aria.Shared.Configuration;
using Aria.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Aria.Api.Services;

/// <summary>
/// The WhatsApp inbox with a human in the loop (wireframe S-07).
///
/// The bot's job is to draft; the human's job is to send. Autonomy is earned per intent, tracked
/// in Insights, time-boxed, and revocable in one click — and escalation is never delegated.
/// </summary>
public sealed class InboxService(
    AriaDbContext db,
    EscalationService escalations,
    PatientCommsService comms,
    IAuditService audit,
    IAriaEventSink events,
    AriaOptions options,
    ILogger<InboxService> logger)
{
    public sealed record InboundResult(
        string MessageId,
        bool Escalated,
        IReadOnlyList<string> Triggers,
        CommsDraft? Draft);

    /// <summary>
    /// Receives a patient message and decides what happens next.
    ///
    /// Note the order: the message is persisted, then the deterministic red-flag detector runs,
    /// and only if it stays silent does any agent see the text. An escalated thread never reaches
    /// the drafting path at all.
    /// </summary>
    public async Task<InboundResult> ReceiveAsync(
        ClinicianIdentity identity, string threadId, string body, CancellationToken ct = default)
    {
        var thread = await db.Threads.FirstAsync(t => t.Id == threadId, ct);

        var inbound = new Message
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            ThreadId = threadId,
            Direction = MessageDirection.Inbound,
            Body = body,
            Status = MessageStatus.Delivered,
            Trust = TrustLevel.UntrustedPatientMessage,   // it is data, never instruction
        };

        db.Messages.Add(inbound);

        // Every inbound message reopens the 24-hour service window.
        thread.ServiceWindowExpiresAt = DateTimeOffset.UtcNow.AddHours(24);
        await db.SaveChangesAsync(ct);

        // ── Stage 1: deterministic, first, always. ──
        var outcome = await escalations.HandleInboundAsync(identity.TenantId, threadId, body, ct);

        if (outcome.Escalated)
        {
            logger.LogCritical("Thread {ThreadId} escalated. Bot muted; no draft will be produced.", threadId);
            return new InboundResult(inbound.Id, true, outcome.Triggers, null);
        }

        // A thread muted by an earlier escalation stays muted until a human resolves it.
        if (thread.BotMuted)
        {
            logger.LogInformation("Thread {ThreadId} is muted pending human review; no draft produced.", threadId);
            return new InboundResult(inbound.Id, false, [], null);
        }

        // ── Stage 2: template-bounded drafting, for a human to approve. ──
        var autonomySettings = await db.AutonomySettings.AsNoTracking()
            .Where(a => a.TenantId == identity.TenantId)
            .ToListAsync(ct);

        var context = new AgentContext(identity, "northbridge-main", thread.PatientId, ThreadId: threadId);
        var draft = await comms.DraftReplyAsync(context, threadId, inbound.Id, new AutonomyPolicy(autonomySettings), ct);

        return new InboundResult(inbound.Id, false, [], draft);
    }

    /// <summary>
    /// Approving is the moment a human takes responsibility, so it is the moment we record one.
    /// The message is queued behind the undo window rather than sent — reversibility as a
    /// schedule, not a recall.
    /// </summary>
    public async Task<Message?> ApproveAsync(
        ClinicianIdentity identity, string messageId, string? editedBody, CancellationToken ct = default)
    {
        var message = await db.Messages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null || message.Status is not MessageStatus.PendingApproval) return message;

        var wasEdited = editedBody is not null && editedBody != message.Body;

        var approved = new Message
        {
            Id = message.Id,
            ThreadId = message.ThreadId,
            Direction = message.Direction,
            Body = editedBody ?? message.Body,
            TemplateId = message.TemplateId,
            Confidence = message.Confidence,
            Basis = message.Basis,
            CreatedAt = message.CreatedAt,
            Status = MessageStatus.Queued,
            ApprovedBy = identity.DoctorId,
            VisibleAfter = DateTimeOffset.UtcNow.AddSeconds(options.Safety.MessageUndoSeconds),
        };

        db.Messages.Remove(message);
        db.Messages.Add(approved);

        var thread = await db.Threads.FirstAsync(t => t.Id == approved.ThreadId, ct);
        thread.Status = ThreadStatus.Open;

        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(identity.TenantId, identity.DoctorId, ActorKind.Clinician,
            AuditActions.MessageApproved, "message", approved.Id, thread.PatientId,
            detail: new { approved.TemplateId, edited = wasEdited, undoSeconds = options.Safety.MessageUndoSeconds },
            ct: ct);

        // Edited-versus-accepted is the signal that decides whether an intent ever earns autonomy.
        events.Emit(wasEdited ? AriaEvents.MessageEdited : AriaEvents.MessageApproved,
            new Dictionary<string, object?>
            {
                ["message_id"] = approved.Id,
                ["thread_id"] = approved.ThreadId,
                ["template_id"] = approved.TemplateId,
                ["edited"] = wasEdited,
            });

        return approved;
    }

    /// <summary>Undo, available for as long as the message is still behind its visibility gate.</summary>
    public async Task<bool> UndoAsync(ClinicianIdentity identity, string messageId, CancellationToken ct = default)
    {
        var message = await db.Messages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null || !message.CanUndo(DateTimeOffset.UtcNow)) return false;

        var threadId = message.ThreadId;
        db.Messages.Remove(message);
        await db.SaveChangesAsync(ct);

        var thread = await db.Threads.FirstAsync(t => t.Id == threadId, ct);
        await audit.WriteAsync(identity.TenantId, identity.DoctorId, ActorKind.Clinician,
            AuditActions.MessageUndone, "message", messageId, thread.PatientId, ct: ct);

        logger.LogInformation("Message {MessageId} undone within the {Seconds}s window.",
            messageId, options.Safety.MessageUndoSeconds);
        return true;
    }
}
