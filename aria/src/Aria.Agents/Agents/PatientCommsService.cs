using Aria.Agents.Models;
using Aria.Agents.Runtime;
using Aria.Agents.Tools;
using Aria.Domain;
using Aria.Domain.Contracts;
using Aria.Domain.Governance;
using Aria.Domain.Messaging;
using Aria.Infrastructure.Persistence;
using Aria.Safety;
using Aria.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aria.Agents.Agents;

public sealed record CommsDraft(
    string? MessageId,
    string? Body,
    string? TemplateId,
    double Confidence,
    string Basis,
    bool NeedsEscalation,
    bool AutoSendPermitted,
    IReadOnlyList<string> Interventions);

/// <summary>
/// Drafts patient replies for a human to approve (wireframe S-07).
///
/// The order here is the whole safety argument for this surface:
///
///   1. The red-flag detector has ALREADY run on the inbound message, upstream and deterministic.
///      If it fired, this service is never reached.
///   2. Generation is template-bounded — the model fills approved blanks and cannot write prose.
///   3. Rendering happens here in C#, from the template row, using validated parameters. The
///      model's text never reaches the patient directly.
///   4. Sending is a separate, authorised action. This method produces a draft and stops.
///
/// A perfect prompt injection against this agent yields a draft with odd parameters, which a
/// human then rejects. That is the point of bounding capability rather than trusting instructions.
/// </summary>
public sealed class PatientCommsService(
    GuardedAgentRunner runner,
    AriaDbContext db,
    ClinicalToolFactory toolFactory,
    IAriaEventSink events,
    ILogger<PatientCommsService> logger)
{
    public async Task<CommsDraft> DraftReplyAsync(
        AgentContext context,
        string threadId,
        string inboundMessageId,
        AutonomyPolicy autonomy,
        CancellationToken ct = default)
    {
        var thread = await db.Threads.FirstAsync(t => t.Id == threadId, ct);
        var inbound = await db.Messages.FirstAsync(m => m.Id == inboundMessageId, ct);
        var patient = await db.Patients.Include(p => p.Flags).FirstAsync(p => p.Id == thread.PatientId, ct);

        var scoped = context with { PatientId = patient.Id, ThreadId = threadId };

        // The patient's own words are untrusted input — fenced, shielded, and unable to originate
        // any tool call that drafts, holds or commits.
        var untrusted = new[]
        {
            new RetrievedDocument(inbound.Id, "Inbound patient message", inbound.Body,
                                  TrustLevel.UntrustedPatientMessage),
        };

        var result = await runner.RunAsync<DraftMessageResult>(
            agentId: AgentIds.PatientComms,
            context: scoped,
            promptId: AgentIds.PatientComms,
            userMessage: "Draft a reply to the patient's message using an approved template.",
            task: ModelTask.MessageDraft,
            tools: toolFactory.ForPatientComms(scoped),
            untrustedInputs: untrusted,
            ct: ct);

        if (!result.Allowed || result.Value is null)
        {
            logger.LogWarning("Comms draft unavailable for thread {ThreadId}: {Reason}", threadId, result.DenialReason);
            return new CommsDraft(null, null, null, 0, "Draft unavailable — compose manually.",
                                  NeedsEscalation: true, AutoSendPermitted: false, result.Interventions);
        }

        var draft = result.Value;

        if (draft.NeedsEscalation || string.IsNullOrWhiteSpace(draft.TemplateId))
        {
            logger.LogInformation("Comms agent declined to draft for thread {ThreadId} — routing to a human.", threadId);
            return new CommsDraft(null, null, null, draft.Confidence,
                                  string.IsNullOrWhiteSpace(draft.Basis) ? "No approved template fits this question." : draft.Basis,
                                  NeedsEscalation: true, AutoSendPermitted: false, result.Interventions);
        }

        // ── Template resolution happens here, in code, against the database row. ──
        var template = await db.MessageTemplates
            .FirstOrDefaultAsync(t => t.Id == draft.TemplateId && t.TenantId == context.TenantId && t.Active, ct);

        if (template is null)
        {
            logger.LogError("Comms agent named template '{TemplateId}', which is not approved for this tenant. Refusing.",
                draft.TemplateId);

            events.Emit(AriaEvents.GuardrailPrefix + "unapproved_template", new Dictionary<string, object?>
            {
                ["thread_id"] = threadId, ["template_id"] = draft.TemplateId,
            });

            return new CommsDraft(null, null, null, draft.Confidence,
                                  "Named template is not approved — escalated.",
                                  NeedsEscalation: true, AutoSendPermitted: false, result.Interventions);
        }

        string body;
        try
        {
            body = template.Render(draft.Parameters);
        }
        catch (ArgumentException ex)
        {
            logger.LogError(ex, "Template '{TemplateId}' parameter validation failed. Refusing to send.", template.Id);
            return new CommsDraft(null, null, template.Id, draft.Confidence,
                                  "Template parameters did not validate — escalated.",
                                  NeedsEscalation: true, AutoSendPermitted: false, result.Interventions);
        }

        var autoPermitted = autonomy.AllowsAutoSend(
            template.Intent, context.Department, context.FacilityId, context.TenantId, DateTimeOffset.UtcNow);

        var message = new Message
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            ThreadId = threadId,
            Direction = MessageDirection.Outbound,
            Body = body,
            TemplateId = template.Id,
            Status = MessageStatus.PendingApproval,
            Confidence = draft.Confidence,
            Basis = draft.Basis,
        };

        db.Messages.Add(message);
        thread.Status = ThreadStatus.NeedsApproval;
        await db.SaveChangesAsync(ct);

        events.Emit(AriaEvents.MessageDrafted, new Dictionary<string, object?>
        {
            ["thread_id"] = threadId,
            ["template_id"] = template.Id,
            ["intent"] = template.Intent,
            ["confidence"] = draft.Confidence,
            ["auto_permitted"] = autoPermitted,
        });

        return new CommsDraft(message.Id, body, template.Id, draft.Confidence, draft.Basis,
                              NeedsEscalation: false, AutoSendPermitted: autoPermitted, result.Interventions);
    }
}
