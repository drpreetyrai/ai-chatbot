using System.Collections.Concurrent;
using System.Text.Json;
using Aria.Domain.Scheduling;
using Microsoft.Extensions.Logging;

namespace Aria.Integrations;

/// <summary>
/// A record of everything the local adapters "sent". Inspectable at
/// <c>GET /v1/admin/integration-inbox</c>, which is how you prove the write barrier works without
/// an EHR, a Google account or a WhatsApp number: sign a note, watch exactly five entries appear,
/// and confirm none appeared before you signed.
/// </summary>
public sealed class LocalIntegrationInbox
{
    private readonly ConcurrentQueue<LocalDelivery> _items = new();

    public void Record(string channel, string idempotencyKey, object payload) =>
        _items.Enqueue(new LocalDelivery(DateTimeOffset.UtcNow, channel, idempotencyKey,
            JsonSerializer.Serialize(payload)));

    public IReadOnlyList<LocalDelivery> All() => [.. _items.Reverse()];
    public void Clear() { while (_items.TryDequeue(out _)) { } }
}

public sealed record LocalDelivery(DateTimeOffset At, string Channel, string IdempotencyKey, string PayloadJson);

// ─────────────────────────────────────────────────────────────────────────────

public sealed class LocalEhrAdapter(LocalIntegrationInbox inbox, ILogger<LocalEhrAdapter> logger) : IEhrAdapter
{
    public string Name => "local-fhir-store";
    public bool IsLive => false;

    public Task<AdapterResult> WriteDocumentAsync(
        string idempotencyKey, string patientMrn, string authorId, string title, string content,
        CancellationToken ct = default)
    {
        var id = $"DocumentReference/{Guid.NewGuid().ToString("n")[..8]}";

        inbox.Record("ehr", idempotencyKey, new
        {
            resourceType = "DocumentReference",
            id,
            status = "current",
            subject = new { identifier = new { value = patientMrn } },
            author = new[] { new { identifier = new { value = authorId } } },
            description = title,
            date = DateTimeOffset.UtcNow,
            content,
        });

        logger.LogInformation("[local EHR] wrote {Id} for MRN {Mrn}", id, patientMrn);
        return Task.FromResult(AdapterResult.Ok(id));
    }
}

/// <summary>
/// An in-memory calendar seeded with a realistic clinic week, so free/busy, buffers and conflict
/// detection are all genuinely exercised. Aria still only writes into slots it holds.
/// </summary>
public sealed class LocalCalendarAdapter(LocalIntegrationInbox inbox, ILogger<LocalCalendarAdapter> logger) : ICalendarAdapter
{
    private readonly ConcurrentDictionary<string, List<CalendarBlock>> _calendars = new();

    public string Name => "local-calendar";
    public bool IsLive => false;

    public Task<IReadOnlyList<CalendarBlock>> GetBusyAsync(
        string calendarId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var blocks = _calendars.GetOrAdd(calendarId, _ => SeedWeek(from));
        IReadOnlyList<CalendarBlock> inRange =
            [.. blocks.Where(b => b.EndAt > from && b.StartAt < to).OrderBy(b => b.StartAt)];
        return Task.FromResult(inRange);
    }

    public Task<AdapterResult> CreateEventAsync(
        string idempotencyKey, string calendarId, DateTimeOffset start, int durationMinutes,
        string title, string? description, CancellationToken ct = default)
    {
        var eventId = $"evt_{Guid.NewGuid().ToString("n")[..10]}";
        var blocks = _calendars.GetOrAdd(calendarId, _ => SeedWeek(start));

        lock (blocks)
        {
            blocks.Add(new CalendarBlock(start, start.AddMinutes(durationMinutes), title, IsExternal: false));
        }

        inbox.Record("calendar", idempotencyKey, new { eventId, calendarId, start, durationMinutes, title, description });
        logger.LogInformation("[local calendar] booked {EventId} at {Start:g}", eventId, start);

        return Task.FromResult(AdapterResult.Ok(eventId));
    }

    public Task<AdapterResult> CancelEventAsync(string calendarId, string eventId, CancellationToken ct = default)
    {
        inbox.Record("calendar", $"cancel:{eventId}", new { eventId, calendarId, action = "cancel" });
        return Task.FromResult(AdapterResult.Ok(eventId));
    }

    /// <summary>External blocks — theatre lists, ward rounds, teaching, lunch — that Aria may never edit.</summary>
    private static List<CalendarBlock> SeedWeek(DateTimeOffset anchor)
    {
        var monday = anchor.Date.AddDays(-(int)anchor.DayOfWeek + (int)DayOfWeek.Monday);
        var offset = anchor.Offset;
        var blocks = new List<CalendarBlock>();

        for (var d = 0; d < 5; d++)
        {
            var day = new DateTimeOffset(monday.AddDays(d), offset);

            blocks.Add(new CalendarBlock(day.AddHours(13), day.AddHours(14), "Lunch", IsExternal: true));

            switch (d)
            {
                case 1:  // Tuesday theatre list
                    blocks.Add(new CalendarBlock(day.AddHours(9), day.AddHours(12), "OT list", IsExternal: true));
                    break;
                case 3:  // Thursday ward round, then teaching
                    blocks.Add(new CalendarBlock(day.AddHours(9), day.AddHours(11), "Ward round", IsExternal: true));
                    blocks.Add(new CalendarBlock(day.AddHours(11), day.AddHours(12), "Teaching", IsExternal: true));
                    break;
            }
        }

        return blocks;
    }
}

public sealed class LocalMessagingAdapter(LocalIntegrationInbox inbox, ILogger<LocalMessagingAdapter> logger) : IMessagingAdapter
{
    public string Name => "local-whatsapp";
    public bool IsLive => false;

    public Task<AdapterResult> SendTemplateAsync(
        string idempotencyKey, string toPhone, string templateName, IReadOnlyList<string> parameters,
        CancellationToken ct = default)
    {
        var wamid = $"wamid.LOCAL{Guid.NewGuid().ToString("n")[..12].ToUpperInvariant()}";
        inbox.Record("whatsapp", idempotencyKey, new { wamid, to = Mask(toPhone), templateName, parameters });
        logger.LogInformation("[local WhatsApp] template {Template} -> {To}", templateName, Mask(toPhone));
        return Task.FromResult(AdapterResult.Ok(wamid));
    }

    public Task<AdapterResult> SendTextAsync(
        string idempotencyKey, string toPhone, string body, CancellationToken ct = default)
    {
        var wamid = $"wamid.LOCAL{Guid.NewGuid().ToString("n")[..12].ToUpperInvariant()}";
        inbox.Record("whatsapp", idempotencyKey, new { wamid, to = Mask(toPhone), body });
        logger.LogInformation("[local WhatsApp] text -> {To}", Mask(toPhone));
        return Task.FromResult(AdapterResult.Ok(wamid));
    }

    /// <summary>Even locally, phone numbers are masked in logs. PHI habits should not be conditional.</summary>
    private static string Mask(string phone) =>
        phone.Length < 6 ? "•••" : string.Concat(phone.AsSpan(0, 3), "••••••", phone.AsSpan(phone.Length - 3));
}
