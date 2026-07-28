using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aria.Domain.Scheduling;
using Aria.Shared.Configuration;
using Microsoft.Extensions.Logging;

namespace Aria.Integrations;

/// <summary>
/// FHIR R4 DocumentReference write.
///
/// Idempotency is carried in the <c>If-None-Exist</c> conditional-create header, so a retry after
/// an ambiguous timeout cannot produce a duplicate note in the patient's record — which is the
/// failure mode clinicians notice and never forgive.
/// </summary>
public sealed class FhirEhrAdapter(
    HttpClient http, AriaOptions options, ILogger<FhirEhrAdapter> logger) : IEhrAdapter
{
    public string Name => "fhir-r4";
    public bool IsLive => true;

    private Azure.Core.AccessToken _token;

    /// <summary>
    /// The service base, with a trailing <c>/metadata</c> removed.
    ///
    /// A FHIR server's capability statement lives at <c>{base}/metadata</c>, and that is
    /// the URL people copy out of the portal because it is the one that returns something
    /// readable in a browser. Left as-is it produces <c>POST {base}/metadata/DocumentReference</c>
    /// and a 405 that says only "operation is not supported" — a genuinely hard error to
    /// diagnose from the message alone.
    /// </summary>
    private string BaseUrl
    {
        get
        {
            var url = options.Fhir.BaseUrl!.TrimEnd('/');
            return url.EndsWith("/metadata", StringComparison.OrdinalIgnoreCase)
                ? url[..^"/metadata".Length]
                : url;
        }
    }

    /// <summary>
    /// Azure Health Data Services requires a Microsoft Entra token; an unauthenticated
    /// request is refused with a bare "Authentication failed".
    ///
    /// Client credentials when they are supplied, DefaultAzureCredential otherwise — which
    /// covers managed identity in production and `az login` on a developer machine, and is
    /// what plan.md §15 specifies (no secrets on disk).
    /// </summary>
    private async Task AuthoriseAsync(CancellationToken ct)
    {
        if (_token.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token.Token);
            return;
        }

        var scope = $"{BaseUrl}/.default";

        Azure.Core.TokenCredential credential =
            !string.IsNullOrWhiteSpace(options.Fhir.ClientId) && !string.IsNullOrWhiteSpace(options.Fhir.ClientSecret)
                ? new Azure.Identity.ClientSecretCredential(
                    options.Identity.TenantId, options.Fhir.ClientId, options.Fhir.ClientSecret)
                : new Azure.Identity.DefaultAzureCredential();

        _token = await credential
            .GetTokenAsync(new Azure.Core.TokenRequestContext([scope]), ct)
            .ConfigureAwait(false);

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token.Token);
    }

    public async Task<AdapterResult> WriteDocumentAsync(
        string idempotencyKey, string patientMrn, string authorId, string title, string content,
        CancellationToken ct = default)
    {
        try
        {
            await AuthoriseAsync(ct).ConfigureAwait(false);

            var resource = new
            {
                resourceType = "DocumentReference",
                status = "current",
                docStatus = "final",
                type = new
                {
                    coding = new[] { new { system = "http://loinc.org", code = "11488-4", display = "Consult note" } },
                },
                subject = new { identifier = new { value = patientMrn } },
                date = DateTimeOffset.UtcNow.ToString("O"),
                author = new[] { new { identifier = new { value = authorId } } },
                description = title,
                identifier = new[] { new { system = "urn:aria:idempotency", value = idempotencyKey } },
                content = new[]
                {
                    new
                    {
                        attachment = new
                        {
                            contentType = "text/plain",
                            data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content)),
                            title,
                        },
                    },
                },
            };

            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{BaseUrl}/DocumentReference")
            {
                Content = JsonContent.Create(resource),
            };

            // Conditional create: the server refuses to make a second copy for the same key.
            request.Headers.TryAddWithoutValidation("If-None-Exist", $"identifier=urn:aria:idempotency|{idempotencyKey}");

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                // Name the two failures that are almost always configuration rather than
                // an outage, so the operator is not left decoding a FHIR OperationOutcome.
                var hint = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                        " — check FHIR_CLIENT_ID/FHIR_CLIENT_SECRET, and that the app registration " +
                        "has the FHIR Data Contributor role on this workspace.",
                    System.Net.HttpStatusCode.MethodNotAllowed =>
                        " — FHIR_BASE_URL should be the service base, without /metadata.",
                    _ => string.Empty,
                };

                logger.LogError("FHIR write failed {Status}: {Body}{Hint}", response.StatusCode, Truncate(body), hint);
                return AdapterResult.Fail($"FHIR {(int)response.StatusCode}: {Truncate(body)}{hint}");
            }

            var location = response.Headers.Location?.ToString();
            if (location is not null) return AdapterResult.Ok(location);

            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
            var id = payload.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

            return AdapterResult.Ok(id is null ? "DocumentReference/unknown" : $"DocumentReference/{id}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FHIR write threw.");
            return AdapterResult.Fail(ex.Message);
        }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : string.Concat(s.AsSpan(0, 299), "…");
}

/// <summary>
/// Google Calendar. The calendar of record — we read everything and write only what we hold.
/// External events surface with <c>IsExternal = true</c> and the UI renders them uneditable, so
/// there is no dual-write and no reconciliation problem to get wrong.
/// </summary>
public sealed class GoogleCalendarAdapter(
    HttpClient http, IGoogleTokenProvider tokens, ILogger<GoogleCalendarAdapter> logger) : ICalendarAdapter
{
    private const string Base = "https://www.googleapis.com/calendar/v3";

    public string Name => "google-calendar";
    public bool IsLive => true;

    public async Task<IReadOnlyList<CalendarBlock>> GetBusyAsync(
        string calendarId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        try
        {
            await AuthoriseAsync(calendarId, ct).ConfigureAwait(false);

            var url = $"{Base}/calendars/{Uri.EscapeDataString(calendarId)}/events" +
                      $"?timeMin={Uri.EscapeDataString(from.ToString("O"))}" +
                      $"&timeMax={Uri.EscapeDataString(to.ToString("O"))}" +
                      "&singleEvents=true&orderBy=startTime";

            var payload = await http.GetFromJsonAsync<JsonElement>(url, ct).ConfigureAwait(false);
            if (!payload.TryGetProperty("items", out var items)) return [];

            var blocks = new List<CalendarBlock>();
            foreach (var item in items.EnumerateArray())
            {
                var start = ReadTime(item, "start");
                var end = ReadTime(item, "end");
                if (start is null || end is null) continue;

                var title = item.TryGetProperty("summary", out var s) ? s.GetString() ?? "(busy)" : "(busy)";

                // Anything Aria did not create is external and therefore read-only here.
                var isOurs = item.TryGetProperty("extendedProperties", out var ep)
                          && ep.TryGetProperty("private", out var priv)
                          && priv.TryGetProperty("ariaManaged", out _);

                blocks.Add(new CalendarBlock(start.Value, end.Value, title, IsExternal: !isOurs));
            }

            return blocks;
        }
        catch (Exception ex)
        {
            // The app never writes blind. A read failure degrades the Schedule screen to
            // read-only with a "Reconnect Google" prompt rather than guessing at availability.
            logger.LogError(ex, "Google Calendar free/busy read failed for {CalendarId}.", calendarId);
            throw new CalendarUnavailableException(calendarId, ex);
        }
    }

    public async Task<AdapterResult> CreateEventAsync(
        string idempotencyKey, string calendarId, DateTimeOffset start, int durationMinutes,
        string title, string? description, CancellationToken ct = default)
    {
        try
        {
            await AuthoriseAsync(calendarId, ct).ConfigureAwait(false);

            var body = new
            {
                summary = title,
                description,
                start = new { dateTime = start.ToString("O") },
                end = new { dateTime = start.AddMinutes(durationMinutes).ToString("O") },
                extendedProperties = new { @private = new { ariaManaged = "1", ariaKey = idempotencyKey } },
            };

            // Google honours a caller-supplied id, which gives us idempotency for free: the same
            // key produces the same event id, and a retry returns 409 rather than a duplicate.
            var eventId = "aria" + Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(idempotencyKey)))[..24];

            using var response = await http.PutAsJsonAsync(
                $"{Base}/calendars/{Uri.EscapeDataString(calendarId)}/events/{eventId}", body, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                logger.LogError("Google Calendar create failed {Status}: {Body}", response.StatusCode, text);
                return AdapterResult.Fail($"Google {(int)response.StatusCode}");
            }

            return AdapterResult.Ok(eventId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Google Calendar create threw.");
            return AdapterResult.Fail(ex.Message);
        }
    }

    public async Task<AdapterResult> CancelEventAsync(string calendarId, string eventId, CancellationToken ct = default)
    {
        try
        {
            await AuthoriseAsync(calendarId, ct).ConfigureAwait(false);
            using var response = await http.DeleteAsync(
                $"{Base}/calendars/{Uri.EscapeDataString(calendarId)}/events/{eventId}", ct).ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? AdapterResult.Ok(eventId)
                : AdapterResult.Fail($"Google {(int)response.StatusCode}");
        }
        catch (Exception ex) { return AdapterResult.Fail(ex.Message); }
    }

    private async Task AuthoriseAsync(string calendarId, CancellationToken ct)
    {
        var token = await tokens.GetAccessTokenAsync(calendarId, ct).ConfigureAwait(false);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static DateTimeOffset? ReadTime(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var node)) return null;

        if (node.TryGetProperty("dateTime", out var dt) && dt.GetString() is { } s)
            return DateTimeOffset.Parse(s);

        // All-day events have a date, not a dateTime. Treat them as a full-day block.
        if (node.TryGetProperty("date", out var d) && d.GetString() is { } ds)
            return new DateTimeOffset(DateTime.Parse(ds), TimeSpan.Zero);

        return null;
    }
}

public sealed class CalendarUnavailableException(string calendarId, Exception inner)
    : Exception($"Calendar '{calendarId}' is unavailable. The schedule is read-only until it reconnects.", inner);

/// <summary>Per-doctor OAuth tokens, stored encrypted. Never a shared service account.</summary>
public interface IGoogleTokenProvider
{
    Task<string> GetAccessTokenAsync(string calendarId, CancellationToken ct = default);
}

/// <summary>
/// WhatsApp Business Cloud API.
///
/// Outside the 24-hour service window only approved templates may be sent, which the caller has
/// already enforced — this adapter exposes both paths so that constraint stays visible in the
/// type system rather than living in someone's memory.
/// </summary>
public sealed class WhatsAppAdapter(
    HttpClient http, AriaOptions options, ILogger<WhatsAppAdapter> logger) : IMessagingAdapter
{
    public string Name => "whatsapp-cloud";
    public bool IsLive => true;

    public Task<AdapterResult> SendTemplateAsync(
        string idempotencyKey, string toPhone, string templateName, IReadOnlyList<string> parameters,
        CancellationToken ct = default) =>
        PostAsync(idempotencyKey, new
        {
            messaging_product = "whatsapp",
            to = toPhone,
            type = "template",
            template = new
            {
                name = templateName,
                language = new { code = "en" },
                components = new[]
                {
                    new
                    {
                        type = "body",
                        parameters = parameters.Select(p => new { type = "text", text = p }).ToArray(),
                    },
                },
            },
        }, ct);

    public Task<AdapterResult> SendTextAsync(
        string idempotencyKey, string toPhone, string body, CancellationToken ct = default) =>
        PostAsync(idempotencyKey, new
        {
            messaging_product = "whatsapp",
            to = toPhone,
            type = "text",
            text = new { body },
        }, ct);

    private async Task<AdapterResult> PostAsync(string idempotencyKey, object payload, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"https://graph.facebook.com/v21.0/{options.WhatsApp.PhoneNumberId}/messages")
            {
                Content = JsonContent.Create(payload),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.WhatsApp.AccessToken);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("WhatsApp send failed {Status}: {Body}", response.StatusCode, text);
                return AdapterResult.Fail($"WhatsApp {(int)response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(text);
            var wamid = doc.RootElement.TryGetProperty("messages", out var messages)
                     && messages.GetArrayLength() > 0
                     && messages[0].TryGetProperty("id", out var id)
                ? id.GetString()
                : null;

            return AdapterResult.Ok(wamid ?? idempotencyKey);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WhatsApp send threw.");
            return AdapterResult.Fail(ex.Message);
        }
    }
}
