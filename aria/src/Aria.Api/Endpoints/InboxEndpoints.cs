using Aria.Api.Auth;
using Aria.Api.Services;
using Aria.Domain;
using Aria.Domain.Messaging;
using Aria.Infrastructure.Persistence;
using Aria.Shared.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Aria.Api.Endpoints;

public static class InboxEndpoints
{
    public static void MapInboxEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/v1/threads");

        group.MapGet("/", async (
            string? filter, HttpContext http, AriaDbContext db, AriaOptions options, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var threads = await db.Threads.AsNoTracking()
                .Where(t => t.TenantId == me.TenantId)
                .ToListAsync(ct);

            threads = filter switch
            {
                "needs_approval" => [.. threads.Where(t => t.Status is ThreadStatus.NeedsApproval)],
                "escalated"      => [.. threads.Where(t => t.Status is ThreadStatus.Escalated)],
                "resolved"       => [.. threads.Where(t => t.Status is ThreadStatus.Resolved)],
                _                => threads,
            };

            var patients = await db.Patients.AsNoTracking()
                .Where(p => threads.Select(t => t.PatientId).Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, ct);

            var lastMessages = await db.Messages.AsNoTracking()
                .Where(m => threads.Select(t => t.Id).Contains(m.ThreadId))
                .ToListAsync(ct);

            var now = DateTimeOffset.UtcNow;

            return Results.Ok(threads
                // Escalated threads pin to the top and stay there until resolved.
                .OrderByDescending(t => t.Status == ThreadStatus.Escalated)
                .ThenByDescending(t => t.Status == ThreadStatus.NeedsApproval)
                .Select(t =>
                {
                    var patient = patients[t.PatientId];
                    var last = lastMessages.Where(m => m.ThreadId == t.Id)
                                           .OrderByDescending(m => m.CreatedAt).FirstOrDefault();
                    return new
                    {
                        t.Id, Status = t.Status.ToString(), t.BotMuted,
                        Patient = new { patient.Id, patient.Name, Phone = patient.MaskedPhone },
                        LastMessage = last?.Body,
                        LastAt = last?.CreatedAt,
                        // The platform constraint that changes behaviour, surfaced in the UI
                        // rather than left in a developer's head (wireframe S-07).
                        WindowRemainingMinutes = t.WindowRemaining(now)?.TotalMinutes is { } m
                            ? (int)m : (int?)null,
                        RequiresTemplate = t.RequiresTemplate(now),
                        PendingApproval = lastMessages.Any(x =>
                            x.ThreadId == t.Id && x.Status == MessageStatus.PendingApproval),
                    };
                }));
        });

        // Open a conversation with a patient. Until now a thread could only exist if the
        // seed created one, which meant there was no way for a coordinator to actually
        // start talking to someone.
        group.MapPost("/", async (
            CreateThreadRequest request, HttpContext http, AriaDbContext db, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var patient = await db.Patients.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == request.PatientId && p.TenantId == me.TenantId, ct);

            if (patient is null) return Results.NotFound(new { error = "Patient not found in this tenant." });

            var thread = new MessageThread
            {
                Id = Guid.NewGuid().ToString("n")[..12],
                TenantId = me.TenantId,
                PatientId = patient.Id,
                Status = ThreadStatus.Open,
                // A newly opened thread has no inbound message yet, so the service window is
                // closed and only approved templates may be sent. That is the WhatsApp rule,
                // and modelling it here keeps the constraint honest from the first message.
                ServiceWindowExpiresAt = null,
            };

            db.Threads.Add(thread);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { thread.Id, Status = thread.Status.ToString(), PatientId = patient.Id });
        });

        group.MapGet("/{id}/messages", async (string id, HttpContext http, AriaDbContext db, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out _)) return Results.Unauthorized();

            var messages = await db.Messages.AsNoTracking()
                .Where(m => m.ThreadId == id)
                .OrderBy(m => m.CreatedAt)
                .ToListAsync(ct);

            var now = DateTimeOffset.UtcNow;

            return Results.Ok(messages.Select(m => new
            {
                m.Id, Direction = m.Direction.ToString(), m.Body, m.TemplateId,
                Status = m.Status.ToString(), m.Confidence, m.Basis, m.CreatedAt, m.SentAt,
                m.ApprovedBy, CanUndo = m.CanUndo(now),
                UndoSecondsRemaining = m.CanUndo(now) && m.VisibleAfter is { } v
                    ? (int)(v - now).TotalSeconds : (int?)null,
            }));
        });

        // ── Simulate an inbound patient message. ──
        // This is the demo lever for the escalation journey: type "chest tightness" as the patient
        // and watch the bot mute, the safety-net reply go out, and the banner appear.
        group.MapPost("/{id}/inbound", async (
            string id, InboundRequest request, HttpContext http, InboxService inbox, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var result = await inbox.ReceiveAsync(me, id, request.Body, ct);

            return Results.Ok(new
            {
                result.MessageId,
                result.Escalated,
                result.Triggers,
                Draft = result.Draft is null ? null : new
                {
                    result.Draft.MessageId,
                    result.Draft.Body,
                    result.Draft.TemplateId,
                    result.Draft.Confidence,
                    result.Draft.Basis,
                    result.Draft.NeedsEscalation,
                    result.Draft.AutoSendPermitted,
                    result.Draft.Interventions,
                },
            });
        });

        group.MapPost("/messages/{messageId}/approve", async (
            string messageId, ApproveRequest request, HttpContext http, InboxService inbox, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var message = await inbox.ApproveAsync(me, messageId, request.EditedBody, ct);
            if (message is null) return Results.NotFound();

            return Results.Ok(new
            {
                message.Id, Status = message.Status.ToString(), message.Body,
                message.ApprovedBy, message.VisibleAfter,
            });
        });

        group.MapPost("/messages/{messageId}/undo", async (
            string messageId, HttpContext http, InboxService inbox, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var undone = await inbox.UndoAsync(me, messageId, ct);
            return undone
                ? Results.Ok(new { undone = messageId })
                : Results.Conflict(new { error = "The undo window has closed — this message is on its way." });
        });

        // ── Escalations. ──
        app.MapGet("/v1/escalations", async (
            HttpContext http, EscalationService escalations, AriaDbContext db,
            AriaOptions options, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var open = await escalations.OpenAsync(me.TenantId, ct);
            var patients = await db.Patients.AsNoTracking()
                .Where(p => open.Select(e => e.PatientId).Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, ct);

            var now = DateTimeOffset.UtcNow;

            return Results.Ok(open.Select(e => new
            {
                e.Id, e.ThreadId, e.Trigger, Severity = e.Severity.ToString(),
                e.RaisedAt, e.DetectorVersion,
                PatientName = patients.GetValueOrDefault(e.PatientId)?.Name ?? "Unknown",
                WaitingSeconds = (int)(now - e.RaisedAt).TotalSeconds,
                // Breach is computed at read time too, so a stale dashboard cannot hide one.
                SlaBreached = e.IsBreached(now, options.Safety.EscalationAckSlaSeconds),
            }));
        });

        app.MapPost("/v1/escalations/{id}/acknowledge", async (
            string id, HttpContext http, EscalationService escalations, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var escalation = await escalations.AcknowledgeAsync(me, id, ct);
            if (escalation is null) return Results.NotFound();

            return Results.Ok(new
            {
                escalation.Id, escalation.AcknowledgedBy, escalation.AcknowledgedAt,
                AckLatencySeconds = escalation.AckLatencySeconds,
            });
        });
    }
}

public sealed record CreateThreadRequest(string PatientId);
public sealed record InboundRequest(string Body);
public sealed record ApproveRequest(string? EditedBody);
