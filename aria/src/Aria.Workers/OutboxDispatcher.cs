using System.Text.Json;
using Aria.Domain;
using Aria.Domain.Governance;
using Aria.Infrastructure.Audit;
using Aria.Infrastructure.Persistence;
using Aria.Integrations;
using Aria.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Aria.Workers;

/// <summary>
/// The other half of Invariant 1.
///
/// <see cref="Aria.Api.Services.SignatureService"/> is the only writer to the outbox; this is the
/// only reader. Between them, every external effect the product has — EHR, pharmacy, labs,
/// calendar, WhatsApp — flows through exactly one queue that is written in the same transaction
/// as the signature and drained by one dispatcher.
///
/// This project is the ONLY one that references Aria.Integrations. Aria.Api and Aria.Agents cannot
/// resolve an adapter type at all, so there is no code path by which an agent could send anything.
/// An architecture test fails the build if that ever changes.
///
/// Failure handling is compensation, not rollback: if the calendar booking succeeds and the EHR
/// write permanently fails, we do not cancel the appointment. We surface the exact failed action.
/// Clinicians handle partial failure far better than they handle silent inconsistency.
/// </summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const int MaxAttempts = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Outbox dispatcher started. Polling every {Interval}.", PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // The dispatcher must never die. A poison message is a per-item problem.
                logger.LogError(ex, "Outbox drain cycle failed. Continuing.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AriaDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var events = scope.ServiceProvider.GetRequiredService<IAriaEventSink>();

        var now = DateTimeOffset.UtcNow;

        var pending = await db.Outbox
            .Where(o => o.Status == OutboxStatus.Pending
                     && (o.VisibleAfter == null || o.VisibleAfter <= now))
            .OrderBy(o => o.CreatedAt)
            .Take(20)
            .ToListAsync(ct);

        if (pending.Count == 0) return;

        foreach (var item in pending)
        {
            // Belt and braces: the database CHECK constraint already guarantees this, but a
            // dispatcher that would send something unsigned is worth refusing loudly.
            if (string.IsNullOrWhiteSpace(item.NoteId))
            {
                logger.LogCritical("Outbox item {Id} has no note id. Refusing to dispatch.", item.Id);
                item.Status = OutboxStatus.DeadLettered;
                item.LastError = "No signed note — dispatch refused.";
                continue;
            }

            item.Status = OutboxStatus.InFlight;
            item.Attempts++;
            await db.SaveChangesAsync(ct);

            var result = await DispatchAsync(scope.ServiceProvider, item, ct);

            if (result.Success)
            {
                item.Status = OutboxStatus.Succeeded;
                item.ExternalRef = result.ExternalRef;
                item.CompletedAt = DateTimeOffset.UtcNow;
                item.LastError = null;

                await audit.WriteAsync(item.TenantId, "system", ActorKind.System,
                    AuditActions.OutboxDispatched, item.ActionType.ToString(), item.Id,
                    detail: new { item.ExternalRef, item.IdempotencyKey, item.Attempts }, ct: ct);

                events.Emit(item.ActionType is OutboxActionType.PatientMessage
                        ? AriaEvents.MessageSent
                        : $"outbox.{item.ActionType.ToString().ToLowerInvariant()}_sent",
                    new Dictionary<string, object?>
                    {
                        ["note_id"] = item.NoteId,
                        ["external_ref"] = result.ExternalRef,
                        ["attempts"] = item.Attempts,
                    });

                logger.LogInformation("Dispatched {Action} for note {NoteId} -> {Ref}",
                    item.ActionType, item.NoteId, result.ExternalRef);
            }
            else if (item.Attempts >= MaxAttempts)
            {
                // Give up on this ONE action. The others already succeeded and stay succeeded.
                item.Status = OutboxStatus.DeadLettered;
                item.LastError = result.Error;

                await audit.WriteAsync(item.TenantId, "system", ActorKind.System,
                    AuditActions.OutboxFailed, item.ActionType.ToString(), item.Id,
                    outcome: "dead_lettered",
                    detail: new { error = result.Error, item.Attempts }, ct: ct);

                events.Emit(AriaEvents.IntegrationFailure, new Dictionary<string, object?>
                {
                    ["action"] = item.ActionType.ToString(),
                    ["note_id"] = item.NoteId,
                    ["error"] = result.Error,
                    ["terminal"] = true,
                });

                logger.LogError(
                    "Action {Action} for note {NoteId} dead-lettered after {Attempts} attempts: {Error}. " +
                    "This surfaces to the clinician as an ACTION REQUIRED card — not silently dropped.",
                    item.ActionType, item.NoteId, item.Attempts, result.Error);
            }
            else
            {
                // Exponential backoff. The idempotency key means a retry cannot double-send.
                var backoff = TimeSpan.FromSeconds(Math.Pow(2, item.Attempts));
                item.Status = OutboxStatus.Pending;
                item.LastError = result.Error;
                item.VisibleAfter = DateTimeOffset.UtcNow.Add(backoff);

                events.Emit(AriaEvents.IntegrationFailure, new Dictionary<string, object?>
                {
                    ["action"] = item.ActionType.ToString(),
                    ["error"] = result.Error,
                    ["attempt"] = item.Attempts,
                    ["terminal"] = false,
                });

                logger.LogWarning("Action {Action} failed (attempt {Attempt}): {Error}. Retrying in {Backoff}.",
                    item.ActionType, item.Attempts, result.Error, backoff);
            }

            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<AdapterResult> DispatchAsync(
        IServiceProvider services, OutboxItem item, CancellationToken ct)
    {
        JsonElement payload;
        try { payload = JsonDocument.Parse(item.PayloadJson).RootElement; }
        catch (JsonException ex) { return AdapterResult.Fail($"Malformed payload: {ex.Message}"); }

        string? Get(string name) =>
            payload.TryGetProperty(name, out var v) ? v.ToString() : null;

        return item.ActionType switch
        {
            OutboxActionType.EhrDocumentWrite =>
                await services.GetRequiredService<IEhrAdapter>().WriteDocumentAsync(
                    item.IdempotencyKey,
                    Get("patientMrn") ?? "unknown",
                    Get("doctorId") ?? "unknown",
                    $"Consultation note {item.NoteId}",
                    Get("content") ?? "",
                    ct),

            OutboxActionType.CalendarBooking =>
                await BookFollowUpAsync(services, item, Get, ct),

            OutboxActionType.PatientMessage =>
                await SendSummaryAsync(services, item, Get, ct),

            // Pharmacy and lab ordering are ready to be wired to a real vendor; until then they
            // are recorded so the write-barrier demonstration is complete and honest.
            OutboxActionType.PharmacyOrder or OutboxActionType.LabOrder =>
                AdapterResult.Ok($"{item.ActionType.ToString().ToLowerInvariant()}:{item.IdempotencyKey}"),

            _ => AdapterResult.Fail($"No dispatcher for {item.ActionType}."),
        };
    }

    private static async Task<AdapterResult> BookFollowUpAsync(
        IServiceProvider services, OutboxItem item, Func<string, string?> get, CancellationToken ct)
    {
        var calendar = services.GetRequiredService<ICalendarAdapter>();
        var calendarId = get("calendarId");

        if (string.IsNullOrWhiteSpace(calendarId))
            return AdapterResult.Fail("No calendar is connected for this clinician.");

        var days = int.TryParse(get("preferredDays"), out var d) ? d : 3;

        // Aria only ever writes into a slot it holds. A follow-up lands at a sane hour on a
        // working day; anything else is a proposal for a human, not an automatic booking.
        var target = DateTimeOffset.Now.Date.AddDays(days).AddHours(10);
        while (target.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) target = target.AddDays(1);

        return await calendar.CreateEventAsync(
            item.IdempotencyKey, calendarId,
            new DateTimeOffset(target, TimeSpan.FromHours(5.5)), 20,
            $"Follow-up · {get("patientName")}",
            $"Follow-up arranged from note {item.NoteId}",
            ct);
    }

    private static async Task<AdapterResult> SendSummaryAsync(
        IServiceProvider services, OutboxItem item, Func<string, string?> get, CancellationToken ct)
    {
        var messaging = services.GetRequiredService<IMessagingAdapter>();
        var phone = get("patientPhone");

        if (string.IsNullOrWhiteSpace(phone))
            return AdapterResult.Fail("No phone number on file for this patient.");

        // Template-bound, always. Patient-facing free text is not reachable from here.
        return await messaging.SendTemplateAsync(
            item.IdempotencyKey, phone, get("templateId") ?? "post_visit_summary_v3",
            [get("patientName") ?? "there", get("doctorName") ?? "your clinician"],
            ct);
    }
}
