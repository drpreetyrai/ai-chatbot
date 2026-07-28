using Aria.Domain.Scheduling;

namespace Aria.Integrations;

/// <summary>
/// The external world, behind three interfaces.
///
/// This assembly is referenced by Aria.Workers and by nothing else. Aria.Agents and Aria.Api
/// cannot resolve these types at all, which is how Invariant 1 — signature is the only write
/// barrier — is enforced at the compiler rather than by convention. An architecture test fails
/// the build if that reference ever appears elsewhere.
///
/// A new vendor is a new adapter, not a new product (wireframe §12).
/// </summary>
public sealed record AdapterResult(bool Success, string? ExternalRef, string? Error)
{
    public static AdapterResult Ok(string externalRef) => new(true, externalRef, null);
    public static AdapterResult Fail(string error) => new(false, null, error);
}

public interface IEhrAdapter
{
    /// <summary>Writes a signed note as a FHIR DocumentReference. Idempotent on the key.</summary>
    Task<AdapterResult> WriteDocumentAsync(
        string idempotencyKey, string patientMrn, string authorId, string title, string content,
        CancellationToken ct = default);

    string Name { get; }
    bool IsLive { get; }
}

public interface ICalendarAdapter
{
    Task<IReadOnlyList<CalendarBlock>> GetBusyAsync(
        string calendarId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    Task<AdapterResult> CreateEventAsync(
        string idempotencyKey, string calendarId, DateTimeOffset start, int durationMinutes,
        string title, string? description, CancellationToken ct = default);

    Task<AdapterResult> CancelEventAsync(string calendarId, string eventId, CancellationToken ct = default);

    string Name { get; }
    bool IsLive { get; }
}

public interface IMessagingAdapter
{
    Task<AdapterResult> SendTemplateAsync(
        string idempotencyKey, string toPhone, string templateName, IReadOnlyList<string> parameters,
        CancellationToken ct = default);

    Task<AdapterResult> SendTextAsync(
        string idempotencyKey, string toPhone, string body, CancellationToken ct = default);

    string Name { get; }
    bool IsLive { get; }
}
